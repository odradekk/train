using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class RowChangeTests
{
    [TestMethod]
    [DataRow(new[] { 1, 2, 3, 4, 5 }, new[] { 1, 2, 4, 5 }, 2, 1, 0)]
    [DataRow(new[] { 1, 2, 4, 5 }, new[] { 1, 2, 3, 4, 5 }, 2, 0, 1)]
    [DataRow(new[] { 1, 2, 3 }, new[] { 1, 9, 3 }, 1, 1, 1)]
    [DataRow(new[] { 1, 2, 3 }, new[] { 1, 2, 3 }, 3, 0, 0)]
    [DataRow(new int[0], new[] { 1, 2 }, 0, 0, 2)]
    [DataRow(new[] { 1, 2 }, new int[0], 0, 2, 0)]
    [DataRow(new[] { 7, 7, 7 }, new[] { 7, 7 }, 2, 1, 0)]
    public void Change_is_the_middle_range_between_the_shared_head_and_tail(int[] old, int[] next, int start, int removed, int inserted) =>
        Assert.AreEqual(new RowChange(start, removed, inserted), RowChange.Between(old, next));

    [TestMethod]
    public void Replacing_the_change_range_turns_the_old_rows_into_the_new_rows()
    {
        var random = new Random(368);
        for (var round = 0; round < 500; round++)
        {
            var old = Enumerable.Range(0, random.Next(0, 12)).Select(_ => random.Next(0, 4)).ToList();
            var next = Enumerable.Range(0, random.Next(0, 12)).Select(_ => random.Next(0, 4)).ToList();
            var change = RowChange.Between(old, next);
            var rows = old.ToList();
            rows.RemoveRange(change.Start, change.Removed);
            rows.InsertRange(change.Start, next.Skip(change.Start).Take(change.Inserted));
            CollectionAssert.AreEqual(next, rows, $"{string.Join(",", old)} -> {string.Join(",", next)}");
        }
    }
}
