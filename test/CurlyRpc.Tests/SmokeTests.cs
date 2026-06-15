using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public void Toolchain_IsWired()
    {
        Assert.AreEqual(-32601, JsonRpcErrorCodes.MethodNotFound);
    }
}
