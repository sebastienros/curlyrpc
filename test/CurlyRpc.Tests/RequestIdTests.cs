using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests;

[TestClass]
public sealed class RequestIdTests
{
    [TestMethod]
    [DataRow("9223372036854775808")]
    [DataRow("-9223372036854775809")]
    [DataRow("1e1000")]
    [DataRow("1e-1000")]
    [DataRow("1e999999999999999999999999999999")]
    [DataRow("1.25")]
    [DataRow("1.0000000000000000000000000000000001")]
    [DataRow("10e-1")]
    public void Converter_RoundTripsNumericToken(string json)
    {
        RequestId id = JsonSerializer.Deserialize<RequestId>(json);
        Assert.IsTrue(id.IsNumber);
        Assert.IsFalse(id.IsNull);
        Assert.IsNull(id.String);
        Assert.AreEqual(json, JsonSerializer.Serialize(id));
        Assert.AreEqual(json, id.ToString());
        Assert.AreNotEqual(new RequestId(json), id);
    }

    [TestMethod]
    [DataRow("9223372036854775808", "9223372036854775808.0")]
    [DataRow("1e1000", "10e999")]
    [DataRow("1.25", "125e-2")]
    [DataRow("0", "-0.0e999999999999999999999999999999")]
    [DataRow("100", "1e2")]
    [DataRow("-10", "-1.00e1")]
    [DataRow("9223372036854775807", "9223372036854775807.0")]
    [DataRow("-9223372036854775808", "-9223372036854775808.0")]
    [DataRow("1e+001000", "10E999")]
    public void EquivalentNumericValues_HaveEqualKeys(string first, string second)
    {
        RequestId a = JsonSerializer.Deserialize<RequestId>(first);
        RequestId b = JsonSerializer.Deserialize<RequestId>(second);
        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.IsTrue(new Dictionary<RequestId, bool> { [a] = true }.ContainsKey(b));
    }

    [TestMethod]
    [DataRow("1", "1.0000000000000000000000000000000001")]
    [DataRow("9223372036854775808", "9223372036854775809")]
    [DataRow("1e1000", "1e1001")]
    public void DistinctNumericValues_RemainDistinct(string first, string second)
        => Assert.AreNotEqual(JsonSerializer.Deserialize<RequestId>(first), JsonSerializer.Deserialize<RequestId>(second));

    [TestMethod]
    public void ExistingLongStringAndNullIds_RetainBehavior()
    {
        Assert.AreEqual(new RequestId(42), JsonSerializer.Deserialize<RequestId>("42"));
        Assert.AreEqual(42L, JsonSerializer.Deserialize<RequestId>("4.2e1").Number);
        Assert.IsNull(JsonSerializer.Deserialize<RequestId>("9223372036854775808").Number);
        Assert.AreEqual("42", JsonSerializer.Serialize(new RequestId(42)));
        Assert.AreEqual("\"42\"", JsonSerializer.Serialize(new RequestId("42")));
        Assert.AreEqual(RequestId.Null, JsonSerializer.Deserialize<RequestId>("null"));
        Assert.AreEqual("null", JsonSerializer.Serialize(RequestId.Null));
        Assert.IsFalse(new RequestId("42").IsNumber);
        Assert.IsTrue(RequestId.Null.IsNull);
    }
}
