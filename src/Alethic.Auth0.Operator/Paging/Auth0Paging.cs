using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Auth0.ManagementApi.Paging;

namespace Alethic.Auth0.Operator.Paging
{

    /// <summary>
    /// Helpers for reading an Auth0 Management API list endpoint in full.
    /// </summary>
    /// <remarks>
    /// Auth0 list endpoints return a single page. Supplying no pagination is not the same as asking
    /// for everything: the API applies its own default of 50 and returns just that first page, so a
    /// caller that does not walk the pages silently sees only the first 50 items.
    /// </remarks>
    public static class Auth0Paging
    {

        /// <summary>
        /// Number of items to request per page. 100 is the maximum Auth0 accepts.
        /// </summary>
        public const int PageSize = 100;

        /// <summary>
        /// Reads every page of a checkpoint-paginated list endpoint.
        /// </summary>
        /// <remarks>
        /// Preferred over <see cref="GetAllOffsetPagesAsync"/> where the endpoint supports it, since
        /// Auth0 has capped how far offset pagination can reach. Confirm the endpoint first: some,
        /// including GET /api/v2/clients and GET /api/v2/connections, reject checkpoint pagination
        /// outright unless the request also carries a q parameter.
        /// </remarks>
        /// <typeparam name="T"></typeparam>
        /// <param name="getPage">Requests a single page for the supplied checkpoint.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Every item across all pages.</returns>
        public static async Task<List<T>> GetAllCheckpointPagesAsync<T>(
            Func<CheckpointPaginationInfo, Task<ICheckpointPagedList<T>>> getPage,
            CancellationToken cancellationToken
        )
        {
            var all = new List<T>();
            string? from = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // CheckpointPaginationInfo treats a null 'from' as "start from the beginning"
#pragma warning disable CS8625
                var page = await getPage(new CheckpointPaginationInfo(PageSize, from!));
#pragma warning restore CS8625
                all.AddRange(page);

                from = page.Paging?.Next;
                if (string.IsNullOrEmpty(from))
                    break;
            }

            return all;
        }

        /// <summary>
        /// Reads every page of an offset-paginated list endpoint.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="getPage">Requests a single page for the supplied offset.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Every item across all pages.</returns>
        public static async Task<List<T>> GetAllOffsetPagesAsync<T>(
            Func<PaginationInfo, Task<IPagedList<T>>> getPage,
            CancellationToken cancellationToken
        )
        {
            var all = new List<T>();

            for (var pageNo = 0; ; pageNo++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await getPage(new PaginationInfo(pageNo, PageSize, true));
                all.AddRange(page);

                // An empty page always means the end, and also guards the loop against a server that
                // never advances. Otherwise prefer Total, which is populated because we ask for
                // include_totals above; a short page is only a reliable stop condition when Total is
                // absent, since the server may cap per_page below what we requested.
                if (page.Count == 0)
                    break;
                if (page.Paging is { } paging)
                {
                    if (all.Count >= paging.Total)
                        break;
                }
                else if (page.Count < PageSize)
                {
                    break;
                }
            }

            return all;
        }

    }

}
