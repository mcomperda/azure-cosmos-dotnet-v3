﻿//-----------------------------------------------------------------------
// <copyright file="DocumentQueryExecutionContextFactory.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Query
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq.Expressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.Internal;
    using Microsoft.Azure.Cosmos.Query.ParallelQuery;
    using Microsoft.Azure.Cosmos.Routing;

    /// <summary>
    /// Factory class for creating the appropriate DocumentQueryExecutionContext for the provided type of query.
    /// </summary>
    internal static class DocumentQueryExecutionContextFactory
    {
        private const int PageSizeFactorForTop = 5;

        public static async Task<IDocumentQueryExecutionContext> CreateDocumentQueryExecutionContextAsync(
            IDocumentQueryClient client,
            ResourceType resourceTypeEnum,
            Type resourceType,
            Expression expression,
            CosmosQueryRequestOptions requestOptions,
            string resourceLink,
            bool isContinuationExpected,
            CancellationToken token,
            Guid correlatedActivityId)
        {
            DocumentQueryExecutionContextBase.InitParams constructorParams = new DocumentQueryExecutionContextBase.InitParams(
                client,
                resourceTypeEnum,
                resourceType,
                expression,
                requestOptions,
                resourceLink,
                false,
                correlatedActivityId);

            CosmosContainerSettings collection = null;
            if (resourceTypeEnum.IsCollectionChild())
            {
                CollectionCache collectionCache = await client.GetCollectionCacheAsync();
                using (
                    DocumentServiceRequest request = DocumentServiceRequest.Create(
                        OperationType.Query,
                        resourceTypeEnum,
                        resourceLink,
                        AuthorizationTokenType.Invalid)) //this request doesnt actually go to server
                {
                    collection = await collectionCache.ResolveCollectionAsync(request, token);
                }
            }

            // For non-Windows platforms(like Linux and OSX) in .NET Core SDK, we cannot use ServiceInterop, so need to bypass in that case.
            // We are also now bypassing this for 32 bit host process running even on Windows as there are many 32 bit apps that will not work without this
            if (CustomTypeExtensions.ByPassQueryParsing())
            {
                // We create a ProxyDocumentQueryExecutionContext that will be initialized with DefaultDocumentQueryExecutionContext
                // which will be used to send the query to Gateway and on getting 400(bad request) with 1004(cross partition query not servable), we initialize it with
                // PipelinedDocumentQueryExecutionContext by providing the partition query execution info that's needed(which we get from the exception returned from Gateway).
                ProxyDocumentQueryExecutionContext proxyQueryExecutionContext =
                    ProxyDocumentQueryExecutionContext.CreateAsync(
                        client,
                        resourceTypeEnum,
                        resourceType,
                        expression,
                        requestOptions,
                        resourceLink,
                        token,
                        collection,
                        isContinuationExpected,
                        correlatedActivityId);

                return proxyQueryExecutionContext;
            }

            DefaultDocumentQueryExecutionContext queryExecutionContext = await DefaultDocumentQueryExecutionContext.CreateAsync(
                constructorParams, isContinuationExpected, token);

            // If isContinuationExpected is false, we want to check if there are aggregates.
            if (
                resourceTypeEnum.IsCollectionChild()
                && resourceTypeEnum.IsPartitioned()
                && (requestOptions.EnableCrossPartitionQuery || !isContinuationExpected))
            {
                //todo:elasticcollections this may rely on information from collection cache which is outdated
                //if collection is deleted/created with same name.
                //need to make it not rely on information from collection cache.
                PartitionedQueryExecutionInfo partitionedQueryExecutionInfo = await queryExecutionContext.GetPartitionedQueryExecutionInfoAsync(
                    collection.PartitionKey,
                    true,
                    isContinuationExpected,
                    token);

                if (DocumentQueryExecutionContextFactory.ShouldCreateSpecializedDocumentQueryExecutionContext(
                        resourceTypeEnum,
                        requestOptions,
                        partitionedQueryExecutionInfo,
                        collection.PartitionKey,
                        isContinuationExpected))
                {
                    List<PartitionKeyRange> targetRanges;
                    if (!string.IsNullOrEmpty(requestOptions.PartitionKeyRangeId))
                    {
                        targetRanges =
                            new List<PartitionKeyRange>
                            {
                                await queryExecutionContext.GetTargetPartitionKeyRangeById(
                                    collection.ResourceId,
                                    requestOptions.PartitionKeyRangeId)
                            };
                    }
                    else
                    {
                        List<Range<string>> queryRanges = partitionedQueryExecutionInfo.QueryRanges;
                        if (requestOptions.PartitionKey != null)
                        {
                            queryRanges = new List<Range<string>>
                            {
                                Range<string>.GetPointRange(
                                    requestOptions.PartitionKey.InternalKey.GetEffectivePartitionKeyString(
                                        collection.PartitionKey))
                            };
                        }
                        else if (TryGetEpkProperty(requestOptions, out string effectivePartitionKeyString))
                        {
                            queryRanges = new List<Range<string>>
                            {
                                Range<string>.GetPointRange(effectivePartitionKeyString)
                            };
                        }

                        targetRanges =
                            await queryExecutionContext.GetTargetPartitionKeyRanges(collection.ResourceId, queryRanges);
                    }



                    return await CreateSpecializedDocumentQueryExecutionContext(
                        constructorParams,
                        partitionedQueryExecutionInfo,
                        targetRanges,
                        collection.ResourceId,
                        isContinuationExpected,
                        token);
                }
            }

            return queryExecutionContext;
        }

        public static async Task<IDocumentQueryExecutionContext> CreateSpecializedDocumentQueryExecutionContext(
            DocumentQueryExecutionContextBase.InitParams constructorParams,
            PartitionedQueryExecutionInfo partitionedQueryExecutionInfo,
            List<PartitionKeyRange> targetRanges,
            string collectionRid,
            bool isContinuationExpected,
            CancellationToken cancellationToken)
        {
            // Figure out the optimal page size.
            long initialPageSize = constructorParams.RequestOptions.MaxItemCount.GetValueOrDefault(ParallelQueryConfig.GetConfig().ClientInternalPageSize);

            if (initialPageSize < -1 || initialPageSize == 0)
            {
                throw new BadRequestException(string.Format(CultureInfo.InvariantCulture, "Invalid MaxItemCount {0}", initialPageSize));
            }

            QueryInfo queryInfo = partitionedQueryExecutionInfo.QueryInfo;

            bool getLazyFeedResponse = queryInfo.HasTop;

            // We need to compute the optimal initial page size for order-by queries
            if (queryInfo.HasOrderBy)
            {
                int top;
                if (queryInfo.HasTop && (top = partitionedQueryExecutionInfo.QueryInfo.Top.Value) > 0)
                {
                    // All partitions should initially fetch about 1/nth of the top value.
                    long pageSizeWithTop = (long)Math.Min(
                        Math.Ceiling(top / (double)targetRanges.Count) * PageSizeFactorForTop,
                        top);

                    if (initialPageSize > 0)
                    {
                        initialPageSize = Math.Min(pageSizeWithTop, initialPageSize);
                    }
                    else
                    {
                        initialPageSize = pageSizeWithTop;
                    }
                }
                else if (isContinuationExpected)
                {
                    if (initialPageSize < 0)
                    {
                        // Max of what the user is willing to buffer and the default (note this is broken if MaxBufferedItemCount = -1)
                        initialPageSize = Math.Max(constructorParams.RequestOptions.MaxBufferedItemCount, ParallelQueryConfig.GetConfig().DefaultMaximumBufferSize);
                    }

                    initialPageSize = (long)Math.Min(
                        Math.Ceiling(initialPageSize / (double)targetRanges.Count) * PageSizeFactorForTop,
                        initialPageSize);
                }
            }

            Debug.Assert(initialPageSize > 0 && initialPageSize <= int.MaxValue,
                string.Format(CultureInfo.InvariantCulture, "Invalid MaxItemCount {0}", initialPageSize));

            return await PipelinedDocumentQueryExecutionContext.CreateAsync(
                constructorParams,
                collectionRid,
                partitionedQueryExecutionInfo,
                targetRanges,
                (int)initialPageSize,
                constructorParams.RequestOptions.RequestContinuation,
                cancellationToken);
        }

        private static bool ShouldCreateSpecializedDocumentQueryExecutionContext(
            ResourceType resourceTypeEnum,
            CosmosQueryRequestOptions requestOptions,
            PartitionedQueryExecutionInfo partitionedQueryExecutionInfo,
            PartitionKeyDefinition partitionKeyDefinition,
            bool isContinuationExpected)
        {
            // We need to aggregate the total results with Pipelined~Context if isContinuationExpected is false.
            return
                (DocumentQueryExecutionContextFactory.IsCrossPartitionQuery(
                    resourceTypeEnum,
                    requestOptions,
                    partitionKeyDefinition,
                    partitionedQueryExecutionInfo) &&
                 (DocumentQueryExecutionContextFactory.IsTopOrderByQuery(partitionedQueryExecutionInfo) ||
                  DocumentQueryExecutionContextFactory.IsAggregateQuery(partitionedQueryExecutionInfo) ||
                  DocumentQueryExecutionContextFactory.IsOffsetLimitQuery(partitionedQueryExecutionInfo) ||
                  DocumentQueryExecutionContextFactory.IsParallelQuery(requestOptions)) ||
                  !string.IsNullOrEmpty(requestOptions.PartitionKeyRangeId)) ||
                  // Even if it's single partition query we create a specialized context to aggregate the aggregates and distinct of distinct.
                  DocumentQueryExecutionContextFactory.IsAggregateQueryWithoutContinuation(
                      partitionedQueryExecutionInfo,
                      isContinuationExpected) ||
                  DocumentQueryExecutionContextFactory.IsDistinctQuery(partitionedQueryExecutionInfo);
        }

        private static bool IsCrossPartitionQuery(
            ResourceType resourceTypeEnum,
            CosmosQueryRequestOptions requestOptions,
            PartitionKeyDefinition partitionKeyDefinition,
            PartitionedQueryExecutionInfo partitionedQueryExecutionInfo)
        {
            return resourceTypeEnum.IsPartitioned()
                && (requestOptions.PartitionKey == null && requestOptions.EnableCrossPartitionQuery)
                && (partitionKeyDefinition.Paths.Count > 0)
                && !(partitionedQueryExecutionInfo.QueryRanges.Count == 1 && partitionedQueryExecutionInfo.QueryRanges[0].IsSingleValue);
        }

        private static bool IsTopOrderByQuery(PartitionedQueryExecutionInfo partitionedQueryExecutionInfo)
        {
            return (partitionedQueryExecutionInfo.QueryInfo != null)
                && (partitionedQueryExecutionInfo.QueryInfo.HasOrderBy || partitionedQueryExecutionInfo.QueryInfo.HasTop);
        }

        private static bool IsAggregateQuery(PartitionedQueryExecutionInfo partitionedQueryExecutionInfo)
        {
            return (partitionedQueryExecutionInfo.QueryInfo != null)
                && (partitionedQueryExecutionInfo.QueryInfo.HasAggregates);
        }

        private static bool IsAggregateQueryWithoutContinuation(PartitionedQueryExecutionInfo partitionedQueryExecutionInfo, bool isContinuationExpected)
        {
            return IsAggregateQuery(partitionedQueryExecutionInfo) && !isContinuationExpected;
        }

        private static bool IsDistinctQuery(PartitionedQueryExecutionInfo partitionedQueryExecutionInfo)
        {
            return partitionedQueryExecutionInfo.QueryInfo.HasDistinct;
        }

        private static bool IsParallelQuery(CosmosQueryRequestOptions requestOptions)
        {
            return (requestOptions.MaxConcurrency != 0);
        }

        private static bool IsOffsetLimitQuery(PartitionedQueryExecutionInfo partitionedQueryExecutionInfo)
        {
            return partitionedQueryExecutionInfo.QueryInfo.HasOffset && partitionedQueryExecutionInfo.QueryInfo.HasLimit;
        }

        private static bool TryGetEpkProperty(CosmosQueryRequestOptions requestOptions, out string effectivePartitionKeyString)
        {
            if (requestOptions?.Properties != null && requestOptions.Properties.TryGetValue(
                   WFConstants.BackendHeaders.EffectivePartitionKeyString, out object effectivePartitionKeyStringObject))
            {
                effectivePartitionKeyString = effectivePartitionKeyStringObject as string;
                if (string.IsNullOrEmpty(effectivePartitionKeyString))
                {
                    throw new ArgumentOutOfRangeException(nameof(effectivePartitionKeyString));
                }

                return true;
            }

            effectivePartitionKeyString = null;
            return false;
        }
    }
}
