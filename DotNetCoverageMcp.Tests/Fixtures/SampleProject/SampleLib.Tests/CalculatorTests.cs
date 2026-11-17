namespace SampleLib.Tests;

public class CalculatorTests
{
    [Fact]
    public void Add_ReturnsSum() => Assert.Equal(3, new Calculator().Add(1, 2));

    [Fact]
    public void Classify_Negative_ReturnsNegative() => Assert.Equal("negative", new Calculator().Classify(-1));
}
