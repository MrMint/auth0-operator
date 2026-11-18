using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Paging;

using Auth0.ManagementApi.Paging;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.Auth0.Operator.Tests
{

    /// <summary>
    /// Tests for <see cref="Auth0Paging"/>.
    /// Auth0 list endpoints return a single page of 50 when asked for no pagination, so every list
    /// read has to walk the pages itself or it silently sees only the first 50 items.
    /// </summary>
    [TestClass]
    public sealed class PaginationTests
    {

        sealed class FakePagedList<T> : List<T>, IPagedList<T>
        {

            public FakePagedList(IEnumerable<T> items, PagingInformation? paging)
                : base(items)
            {
                Paging = paging!;
            }

            public PagingInformation Paging { get; set; }

        }

        sealed class FakeCheckpointPagedList<T> : List<T>, ICheckpointPagedList<T>
        {

            public FakeCheckpointPagedList(IEnumerable<T> items, string? next)
                : base(items)
            {
                Paging = new CheckpointPagingInformation(next!);
            }

            public CheckpointPagingInformation Paging { get; set; }

        }

        [TestMethod]
        public async Task OffsetPagingCollectsEveryPage()
        {
            const int Total = 250;
            var source = Enumerable.Range(0, Total).ToArray();
            var requested = new List<int>();

            var all = await Auth0Paging.GetAllOffsetPagesAsync<int>(pagination =>
            {
                requested.Add(pagination.PageNo);
                var items = source.Skip(pagination.PageNo * pagination.PerPage).Take(pagination.PerPage);
                return Task.FromResult<IPagedList<int>>(
                    new FakePagedList<int>(items, new PagingInformation(0, 0, 0, Total)));
            }, CancellationToken.None);

            CollectionAssert.AreEqual(source, all, "Every item across all pages must be returned.");
            Assert.IsTrue(requested.Count > 1, "More than one page should have been requested.");
        }

        [TestMethod]
        public async Task OffsetPagingKeepsGoingWhenServerCapsPageSize()
        {
            // Auth0 may return fewer items than the per_page we asked for. A short page must not be
            // mistaken for the end of the list while Total says otherwise.
            const int Total = 120;
            const int ServerCap = 25;
            var source = Enumerable.Range(0, Total).ToArray();

            var all = await Auth0Paging.GetAllOffsetPagesAsync<int>(pagination =>
            {
                var items = source.Skip(pagination.PageNo * ServerCap).Take(ServerCap);
                return Task.FromResult<IPagedList<int>>(
                    new FakePagedList<int>(items, new PagingInformation(0, 0, 0, Total)));
            }, CancellationToken.None);

            CollectionAssert.AreEqual(source, all, "A server-capped page size must not truncate the read.");
        }

        [TestMethod]
        public async Task OffsetPagingStopsOnEmptyPage()
        {
            // Guards against looping forever when the server reports a Total it never delivers.
            var pages = 0;

            var all = await Auth0Paging.GetAllOffsetPagesAsync<int>(_ =>
            {
                pages++;
                return Task.FromResult<IPagedList<int>>(
                    new FakePagedList<int>([], new PagingInformation(0, 0, 0, 999)));
            }, CancellationToken.None);

            Assert.AreEqual(0, all.Count);
            Assert.AreEqual(1, pages, "An empty page must end the loop immediately.");
        }

        [TestMethod]
        public async Task CheckpointPagingFollowsNextUntilExhausted()
        {
            var pages = new Dictionary<string, (int[] Items, string? Next)>
            {
                [""] = ([1, 2], "a"),
                ["a"] = ([3, 4], "b"),
                ["b"] = ([5], null),
            };

            var all = await Auth0Paging.GetAllCheckpointPagesAsync<int>(pagination =>
            {
                var (items, next) = pages[pagination.From ?? ""];
                return Task.FromResult<ICheckpointPagedList<int>>(new FakeCheckpointPagedList<int>(items, next));
            }, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, all);
        }

        [TestMethod]
        public async Task CheckpointPagingStartsFromTheBeginning()
        {
            string? seen = "unset";

            await Auth0Paging.GetAllCheckpointPagesAsync<int>(pagination =>
            {
                seen = pagination.From;
                return Task.FromResult<ICheckpointPagedList<int>>(new FakeCheckpointPagedList<int>([], null));
            }, CancellationToken.None);

            Assert.IsNull(seen, "The first request must not carry a checkpoint.");
        }

    }

}
