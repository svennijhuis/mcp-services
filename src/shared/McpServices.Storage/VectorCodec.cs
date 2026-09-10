using System.Buffers.Binary;
using System.Numerics;

namespace McpServices.Storage;

/// <summary>Float vectors as little-endian float32 blobs (SQLite) plus in-process similarity.</summary>
public static class VectorCodec
{
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * sizeof(float)));
        }

        return vector;
    }

    /// <summary>Cosine similarity in [-1, 1]; 0 for mismatched or empty vectors.</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0f;
        }

        var dot = 0f;
        var normA = 0f;
        var normB = 0f;
        var i = 0;
        var width = Vector<float>.Count;
        if (a.Length >= width)
        {
            var vDot = Vector<float>.Zero;
            var vA = Vector<float>.Zero;
            var vB = Vector<float>.Zero;
            for (; i <= a.Length - width; i += width)
            {
                var va = new Vector<float>(a.Slice(i, width));
                var vb = new Vector<float>(b.Slice(i, width));
                vDot += va * vb;
                vA += va * va;
                vB += vb * vb;
            }

            dot = Vector.Sum(vDot);
            normA = Vector.Sum(vA);
            normB = Vector.Sum(vB);
        }

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0f || normB == 0f)
        {
            return 0f;
        }

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        var sum = 0f;
        foreach (var v in vector)
        {
            sum += v * v;
        }

        var result = new float[vector.Length];
        if (sum == 0f)
        {
            return result;
        }

        var norm = MathF.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = vector[i] / norm;
        }

        return result;
    }
}
