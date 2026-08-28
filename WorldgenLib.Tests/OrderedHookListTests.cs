using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace WorldgenLib.Tests
{
    public class OrderedHookListTests
    {
        [Fact]
        public void Register_AcceptsDelegates()
        {
            var list = new OrderedHookList<string>();
            list.Register("mod-a", 10, "handler-a");
            list.Register("mod-b", 5, "handler-b");
            Assert.Equal(2, list.Count);
        }

        [Fact]
        public void Register_ThrowsOnNullDelegate()
        {
            var list = new OrderedHookList<Action>();
            Assert.Throws<ArgumentNullException>(() =>
                list.Register("mod-a", 0, null!));
        }

        [Fact]
        public void Freeze_SortsByOrderThenIndex()
        {
            var list = new OrderedHookList<string>();
            list.Register("second", 10, "b");
            list.Register("first", 5, "a");
            list.Register("third", 10, "c");  // same order as "second"

            list.Freeze();

            var results = list.Enumerate().ToList();
            Assert.Equal(3, results.Count);
            Assert.Equal("a", results[0].Delegate);   // order 5
            Assert.Equal("b", results[1].Delegate);   // order 10, registered first
            Assert.Equal("c", results[2].Delegate);   // order 10, registered second
        }

        [Fact]
        public void Freeze_PreventsLateRegistration()
        {
            var list = new OrderedHookList<string>();
            list.Register("mod-a", 0, "a");
            list.Freeze();

            Assert.Throws<InvalidOperationException>(() =>
                list.Register("mod-b", 5, "b"));
        }

        [Fact]
        public void Enumerate_ThrowsBeforeFreeze()
        {
            var list = new OrderedHookList<string>();
            list.Register("mod-a", 0, "a");

            Assert.Throws<InvalidOperationException>(() =>
                list.Enumerate().ToList());
        }

        [Fact]
        public void GetRegistrationReport_ReturnsAllEntries()
        {
            var list = new OrderedHookList<string>();
            list.Register("mod-a", 10, "a");
            list.Register("mod-b", 5, "b");
            list.Freeze();

            var report = list.GetRegistrationReport();
            Assert.Equal(2, report.Count);
            // Report returns sorted order (after freeze): mod-b (order 5) first
            Assert.Equal("mod-b", report[0].ModId);
            Assert.Equal("mod-a", report[1].ModId);
        }

        [Fact]
        public void Snapshot_IsFrozenAndOrdered()
        {
            var list = new OrderedHookList<string>();
            list.Register("late", 10, "late");
            list.Register("early", 1, "early");
            list.Freeze();

            var snapshot = list.Snapshot;
            Assert.Equal(2, snapshot.Length);
            Assert.Equal("early", snapshot[0].ModId);
            Assert.Equal("late", snapshot[1].ModId);
            Assert.Equal("early", snapshot[0].Handler);
        }

        [Fact]
        public void Snapshot_ReflectsDisabledOwnerWithoutCopyingEntries()
        {
            var list = new OrderedHookList<string>();
            list.Register("bad", 0, "bad-handler");
            list.Register("good", 1, "good-handler");
            list.Freeze();

            Assert.True(list.Disable("bad"));
            var snapshot = list.Snapshot;
            Assert.True(list.IsDisabled("bad"));
            Assert.False(list.IsDisabled("good"));
            Assert.Equal(2, snapshot.Length);
        }
    }
}
