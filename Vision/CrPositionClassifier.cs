using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FH6OpenAssist.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FH6OpenAssist.Vision;

public enum CrPositionLabel
{
    Invalid,
    Valid,
    Unknown
}

public sealed record CrPositionPrediction(
    CrPositionLabel Label,
    double ValidProbability,
    int Frames,
    double MinimumValidProbability,
    double MaximumValidProbability,
    TimeSpan Elapsed);

public sealed class CrPositionClassifier(
    AutomationSettings settings,
    AutomationLogger logger) : IDisposable
{
    private const double MinimumValidAuthorizationThreshold = 0.90;
    private readonly object _sessionLock = new();
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;
    private bool _disposed;

    public async Task<double> PredictValidProbabilityAsync(
        Bitmap frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        using var prepared = PrepareInput(frame);
        var tensor = CreateTensor(prepared);
        return await Task.Run(
            () => PredictCore(tensor, cancellationToken),
            cancellationToken);
    }

    public CrPositionPrediction Aggregate(
        IReadOnlyCollection<double> validProbabilities,
        TimeSpan elapsed)
    {
        if (validProbabilities.Count == 0)
        {
            throw new ArgumentException("Ao menos uma previsão é necessária.", nameof(validProbabilities));
        }

        var minimum = validProbabilities.Min();
        var maximum = validProbabilities.Max();
        var average = validProbabilities.Average();
        var cr = settings.CrFarm;
        var validThreshold = Math.Max(
            MinimumValidAuthorizationThreshold,
            cr.ValidThreshold);

        // Falso positivo é o erro caro. Uma posição só é válida quando todos
        // os frames ultrapassam o limiar alto. O inverso usa um limiar baixo;
        // a faixa intermediária permanece Unknown e não dispara entradas.
        var label = minimum >= validThreshold
            ? CrPositionLabel.Valid
            : maximum <= cr.InvalidThreshold
                ? CrPositionLabel.Invalid
                : CrPositionLabel.Unknown;

        return new CrPositionPrediction(
            label,
            average,
            validProbabilities.Count,
            minimum,
            maximum,
            elapsed);
    }

    private double PredictCore(DenseTensor<float> tensor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = GetOrCreateSession();
        var input = NamedOnnxValue.CreateFromTensor(_inputName!, tensor);
        using var results = session.Run([input], [_outputName!]);
        cancellationToken.ThrowIfCancellationRequested();

        var logits = results.First().AsEnumerable<float>().Take(2).ToArray();
        if (logits.Length != 2 || logits.Any(value => !float.IsFinite(value)))
        {
            throw new AutomationFaultException(
                "O modelo de posição do Farm de CR retornou uma saída inválida.");
        }

        var maximum = Math.Max(logits[0], logits[1]);
        var invalid = Math.Exp(logits[0] - maximum);
        var valid = Math.Exp(logits[1] - maximum);
        return valid / (invalid + valid);
    }

    private InferenceSession GetOrCreateSession()
    {
        lock (_sessionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                return _session;
            }

            if (!File.Exists(settings.CrPositionModelPath))
            {
                throw new CalibrationRequiredException(
                    $"Modelo ONNX do Farm de CR não encontrado: {settings.CrPositionModelPath}");
            }

            var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = 1,
                EnableCpuMemArena = false,
                EnableMemoryPattern = false,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };
            _session = new InferenceSession(settings.CrPositionModelPath, options);
            _inputName = _session.InputMetadata.Keys.Single();
            _outputName = _session.OutputMetadata.Keys.Single();
            logger.Info(
                $"Modelo ONNX de posição carregado em CPU/1 thread: " +
                $"{Path.GetFileName(settings.CrPositionModelPath)} ({settings.CrFarm.InputWidth}x{settings.CrFarm.InputHeight}).");
            return _session;
        }
    }

    private Bitmap PrepareInput(Bitmap source)
    {
        var cr = settings.CrFarm;
        var cropX = Math.Clamp((int)Math.Round(source.Width * cr.CropX), 0, source.Width - 1);
        var cropY = Math.Clamp((int)Math.Round(source.Height * cr.CropY), 0, source.Height - 1);
        var cropWidth = Math.Clamp(
            (int)Math.Round(source.Width * cr.CropWidth),
            1,
            source.Width - cropX);
        var cropHeight = Math.Clamp(
            (int)Math.Round(source.Height * cr.CropHeight),
            1,
            source.Height - cropY);

        var prepared = new Bitmap(cr.InputWidth, cr.InputHeight, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(prepared);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, prepared.Width, prepared.Height),
            new Rectangle(cropX, cropY, cropWidth, cropHeight),
            GraphicsUnit.Pixel);
        return prepared;
    }

    private static DenseTensor<float> CreateTensor(Bitmap bitmap)
    {
        var tensor = new DenseTensor<float>([1, 3, bitmap.Height, bitmap.Width]);
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[Math.Abs(stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            // O treino usa RGB /255 seguido de mean=.5/std=.5.
            const float scale = 2f / 255f;
            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = stride >= 0
                    ? y * stride
                    : (bitmap.Height - 1 - y) * -stride;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = row + x * 3;
                    tensor[0, 0, y, x] = bytes[pixel + 2] * scale - 1f;
                    tensor[0, 1, y, x] = bytes[pixel + 1] * scale - 1f;
                    tensor[0, 2, y, x] = bytes[pixel] * scale - 1f;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return tensor;
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }
}
