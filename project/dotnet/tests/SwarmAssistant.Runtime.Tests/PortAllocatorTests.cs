namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Agents;

public sealed class PortAllocatorTests
{
    [Fact]
    public void Allocate_ReturnsPortsInRange()
    {
        var allocator = new PortAllocator("8001-8032");

        var port1 = allocator.Allocate();
        var port2 = allocator.Allocate();

        Assert.Equal(8001, port1);
        Assert.Equal(8002, port2);
    }

    [Fact]
    public void Release_MakesPortAvailable()
    {
        var allocator = new PortAllocator("8001-8003");

        var p1 = allocator.Allocate();
        var p2 = allocator.Allocate();
        allocator.Release(p1);
        var p3 = allocator.Allocate();

        Assert.Equal(p1, p3);
    }

    [Fact]
    public void Allocate_ThrowsWhenExhausted()
    {
        var allocator = new PortAllocator("8001-8002");

        allocator.Allocate();
        allocator.Allocate();

        Assert.Throws<InvalidOperationException>(() => allocator.Allocate());
    }

    [Fact]
    public void Parse_HandlesRangeFormat()
    {
        var allocator = new PortAllocator("9000-9004");
        Assert.Equal(9000, allocator.Allocate());
    }
}
