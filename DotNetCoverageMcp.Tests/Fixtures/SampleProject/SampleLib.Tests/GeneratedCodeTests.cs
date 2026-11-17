namespace SampleLib.Tests;

// Exercises the members whose code the compiler moves into generated classes. Each call
// takes only one side of its branch, so every one of them has an uncovered branch left.
public class GeneratedCodeTests
{
    [Fact]
    public async Task AsyncAndLambdaMembers_Run()
    {
        var c = new Calculator();
        Assert.Equal(3, await c.AddAsync(1, 2));
        Assert.Equal("positive", await c.DescribeAsync(5));
        Assert.Equal("short", await c.DescribeAsync("ab"));
        Assert.Equal(1, c.CountPositive([1]));
        Assert.Equal(5, c.SumAbove([5], 1));
        Assert.Equal(6, c.Twice(3));
    }
}
