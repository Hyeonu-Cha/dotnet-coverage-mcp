namespace SampleLib;

public class Calculator
{
    public int Add(int a, int b) => a + b;

    public string Classify(int n)
    {
        if (n < 0) return "negative";
        if (n == 0) return "zero";
        return "positive";
    }
}
