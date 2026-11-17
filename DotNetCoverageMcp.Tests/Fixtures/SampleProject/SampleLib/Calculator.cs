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

    // The members below exist for the async/lambda normalisation tests: the compiler moves
    // each of them, wholly or partly, into generated classes and methods.

    public async Task<int> AddAsync(int a, int b)
    {
        await Task.Yield();
        if (a < 0) return -1;
        return a + b;
    }

    public async Task<string> DescribeAsync(int n)
    {
        await Task.Yield();
        return Classify(n);
    }

    public async Task<string> DescribeAsync(string s)
    {
        await Task.Yield();
        return s.Length > 3 ? "long" : "short";
    }

    // Static lambda: lives in the shared <>c class.
    public int CountPositive(IEnumerable<int> xs) => xs.Count(x => x > 0);

    // Capturing lambda: lives in a <>c__DisplayClass closure class.
    public int SumAbove(IEnumerable<int> xs, int min) => xs.Where(x => x > min).Sum();

    // Local function: compiled to a <Twice>g__Double|N_0 method.
    public int Twice(int n)
    {
        return Double(n);

        static int Double(int x) => x > 100 ? 100 : x * 2;
    }
}
