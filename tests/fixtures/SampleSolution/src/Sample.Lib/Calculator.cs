using System.Text;
using System.Collections.Generic;

namespace Sample.Lib;

/// <summary>Adds and multiplies numbers.</summary>
public interface ICalculator
{
    /// <summary>Adds two numbers.</summary>
    /// <param name="a">First operand.</param>
    /// <param name="b">Second operand.</param>
    /// <returns>The sum.</returns>
    int Add(int a, int b);

    int Multiply(int a, int b);
}

/// <summary>Default <see cref="ICalculator"/> implementation.</summary>
public class Calculator : ICalculator
{
    private int _calls;

    public int Calls => _calls;

    /// <inheritdoc />
    public int Add(int a, int b)
    {
        _calls++;
        return a + b;
    }

    public int Multiply(int a, int b)
    {
        _calls++;
        return a * b;
    }

    public virtual string Describe(int value)
    {
        if (value < 0)
        {
            return "negative";
        }
        else if (value == 0)
        {
            return "zero";
        }

        return value switch
        {
            < 10 => "small",
            < 100 and > 9 => "medium",
            _ => "large",
        };
    }

    private int NeverCalled() => 42;
}

public sealed class ScientificCalculator : Calculator
{
    public override string Describe(int value) => "sci:" + base.Describe(value);
}
