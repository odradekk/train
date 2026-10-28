using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Train.SceneScale.Tests;
[TestClass]
[TestCategory("fast")]
public sealed class CountsTests
{
    [TestMethod]
    public void Negative_coordinates_use_floor_chunks()
    {
        var result = Probe.CountFar([new HashSet<(int,int,int)> { (-1,0,193), (0,0,193) }]);
        Assert.AreEqual((2L, 2, 10L), (result.N, result.C, result.F));
    }
    [TestMethod]
    public void Radius_boundary_is_near_and_neighbor_still_hides_a_face()
    {
        var result = Probe.CountFar([new HashSet<(int,int,int)> { (192,0,0), (193,0,0) }]);
        Assert.AreEqual((1L, 1, 5L), (result.N, result.C, result.F));
    }
    [TestMethod]
    public void Separate_buildings_do_not_hide_faces_and_share_one_chunk()
    {
        var result = Probe.CountFar([new HashSet<(int,int,int)> { (193,0,0) }, new HashSet<(int,int,int)> { (194,0,0) }]);
        Assert.AreEqual((2L, 1, 12L), (result.N, result.C, result.F));
    }
}
