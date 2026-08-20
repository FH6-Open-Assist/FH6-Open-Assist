using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FH6OpenAssist.Vision;

public enum ClassicalGameStateKind
{
    Unknown,
    StreetMenu,
    EventMenu,
    EventPreRaceMenu,
    ConfirmationDialog,
    ControllerDisconnected
}

public sealed record ClassicalGameStateResult(
    ClassicalGameStateKind Kind,
    double Confidence,
    string Evidence,
    TimeSpan Elapsed);

/// <summary>
/// Reconhece somente telas com geometria e cores estáveis. É uma segunda
/// opinião para o OCR nos checkpoints já existentes, não um loop de captura.
/// O ONNX continua sendo a autoridade exclusiva para a posição do carro.
/// </summary>
public sealed class ClassicalGameStateDetector
{
    private static readonly NormalizedRegion LeftMenuTile = new(0.12, 0.24, 0.27, 0.78);
    private static readonly NormalizedRegion RightMenuTile = new(0.72, 0.24, 0.87, 0.78);
    private static readonly NormalizedRegion LeftEventIcon = new(0.13, 0.27, 0.25, 0.50);
    private static readonly NormalizedRegion RightEventIcon = new(0.74, 0.27, 0.85, 0.50);
    private static readonly NormalizedRegion DialogHeader = new(0.32, 0.42, 0.68, 0.51);
    private static readonly NormalizedRegion DialogBody = new(0.32, 0.50, 0.68, 0.58);
    private static readonly NormalizedRegion ConfirmationHeader = new(0.32, 0.37, 0.68, 0.46);
    private static readonly NormalizedRegion ConfirmationBody = new(0.32, 0.46, 0.68, 0.63);
    private static readonly NormalizedRegion PreRaceList = new(0.035, 0.60, 0.23, 0.85);
    private static readonly NormalizedRegion PreRaceTopBar = new(0, 0, 1, 0.12);
    private static readonly NormalizedRegion PreRaceBottomBar = new(0, 0.87, 1, 1);

    public ClassicalGameStateResult Analyze(Bitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width < 320 || frame.Height < 180)
        {
            return new ClassicalGameStateResult(
                ClassicalGameStateKind.Unknown,
                0,
                "frame pequeno demais para validar o layout",
                TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();

        // Diálogo central: cabeçalho verde-limão contínuo e corpo escuro.
        // Verificado antes dos menus para falhar fechado durante overlays.
        var dialogLime = Ratio(frame, DialogHeader, IsLime);
        var dialogDark = Ratio(frame, DialogBody, IsDark);
        if (dialogLime >= 0.45 && dialogDark >= 0.70)
        {
            stopwatch.Stop();
            return Result(
                ClassicalGameStateKind.ControllerDisconnected,
                0.98,
                $"diálogo central estável: cabeçalho verde={dialogLime:P0}, corpo escuro={dialogDark:P0}",
                stopwatch.Elapsed);
        }

        var confirmationLime = Ratio(frame, ConfirmationHeader, IsLime);
        var confirmationDark = Ratio(frame, ConfirmationBody, IsDark);
        var confirmationWhite = Ratio(frame, ConfirmationBody, IsWhite);
        if (confirmationLime >= 0.55 &&
            confirmationDark >= 0.50 &&
            confirmationWhite >= 0.10)
        {
            stopwatch.Stop();
            return Result(
                ClassicalGameStateKind.ConfirmationDialog,
                0.98,
                $"modal de confirmação: cabeçalho verde={confirmationLime:P0}, " +
                $"corpo escuro/branco={confirmationDark:P0}/{confirmationWhite:P0}",
                stopwatch.Elapsed);
        }

        // Menu pré-corrida: barras pretas em toda a largura e a lista vertical
        // branca de cinco opções à esquerda. O layout independe do carro,
        // pintura, título do evento e cenário de fundo.
        var preRaceListWhite = Ratio(frame, PreRaceList, IsWhite);
        var preRaceTopDark = Ratio(frame, PreRaceTopBar, IsDark);
        var preRaceBottomDark = Ratio(frame, PreRaceBottomBar, IsDark);
        if (preRaceListWhite >= 0.50 &&
            preRaceTopDark >= 0.80 &&
            preRaceBottomDark >= 0.80)
        {
            stopwatch.Stop();
            return Result(
                ClassicalGameStateKind.EventPreRaceMenu,
                0.99,
                $"menu pré-corrida: lista branca={preRaceListWhite:P0}, " +
                $"barras escuras={preRaceTopDark:P0}/{preRaceBottomDark:P0}",
                stopwatch.Elapsed);
        }

        // Menu da rua: os cartões laterais magenta e laranja são grandes,
        // fixos e independentes de texto, mapa, carro ou resolução.
        var leftMagenta = Ratio(frame, LeftMenuTile, IsMagenta);
        var rightOrange = Ratio(frame, RightMenuTile, IsOrange);
        if (leftMagenta >= 0.45 && rightOrange >= 0.45)
        {
            stopwatch.Stop();
            return Result(
                ClassicalGameStateKind.StreetMenu,
                0.98,
                $"cartões laterais: magenta={leftMagenta:P0}, laranja={rightOrange:P0}",
                stopwatch.Elapsed);
        }

        // Menu do evento: dois cartões brancos, cada um com ícone limão.
        var leftWhite = Ratio(frame, LeftMenuTile, IsWhite);
        var rightWhite = Ratio(frame, RightMenuTile, IsWhite);
        if (leftWhite >= 0.55 && rightWhite >= 0.55)
        {
            var leftLime = Ratio(frame, LeftEventIcon, IsLime);
            var rightLime = Ratio(frame, RightEventIcon, IsLime);
            if (leftLime >= 0.035 && rightLime >= 0.035)
            {
                stopwatch.Stop();
                return Result(
                    ClassicalGameStateKind.EventMenu,
                    0.98,
                    $"cartões brancos={leftWhite:P0}/{rightWhite:P0}, ícones verdes={leftLime:P1}/{rightLime:P1}",
                    stopwatch.Elapsed);
            }
        }

        stopwatch.Stop();
        return Result(
            ClassicalGameStateKind.Unknown,
            0,
            $"sem layout clássico estável; diálogo={dialogLime:P0}/{dialogDark:P0}, " +
            $"confirmação={confirmationLime:P0}/{confirmationDark:P0}/{confirmationWhite:P0}, " +
            $"rua={leftMagenta:P0}/{rightOrange:P0}, evento={leftWhite:P0}/{rightWhite:P0}",
            stopwatch.Elapsed);
    }

    private static ClassicalGameStateResult Result(
        ClassicalGameStateKind kind,
        double confidence,
        string evidence,
        TimeSpan elapsed) =>
        new(kind, Math.Clamp(confidence, 0, 1), evidence, elapsed);

    private static double Ratio(
        Bitmap source,
        NormalizedRegion normalized,
        Func<int, int, int, bool> predicate)
    {
        var region = ToPixels(source, normalized);
        var supported = source.PixelFormat is PixelFormat.Format24bppRgb or
            PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or
            PixelFormat.Format32bppRgb;
        if (!supported)
        {
            using var converted = source.Clone(
                new Rectangle(0, 0, source.Width, source.Height),
                PixelFormat.Format24bppRgb);
            return Ratio(converted, normalized, predicate);
        }

        var data = source.LockBits(region, ImageLockMode.ReadOnly, source.PixelFormat);
        try
        {
            var bytesPerPixel = Image.GetPixelFormatSize(source.PixelFormat) / 8;
            var rowBytes = Math.Abs(data.Stride);
            var bytes = new byte[rowBytes * region.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var matched = 0;
            var sampled = 0;

            // A subamostragem 2x2 reduz o custo em 75%; os alvos ocupam áreas
            // grandes, então não dependem de detalhes de um pixel.
            for (var y = 0; y < region.Height; y += 2)
            {
                var row = data.Stride >= 0
                    ? y * data.Stride
                    : (region.Height - 1 - y) * -data.Stride;
                for (var x = 0; x < region.Width; x += 2)
                {
                    var pixel = row + x * bytesPerPixel;
                    sampled++;
                    if (predicate(bytes[pixel + 2], bytes[pixel + 1], bytes[pixel]))
                    {
                        matched++;
                    }
                }
            }

            return sampled == 0 ? 0 : (double)matched / sampled;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private static bool IsMagenta(int red, int green, int blue) =>
        red >= 180 && green <= 130 && blue >= 70 && red >= blue;

    private static bool IsOrange(int red, int green, int blue) =>
        red >= 190 && green >= 60 && green <= 180 && blue <= 100 && red >= green + 40;

    private static bool IsWhite(int red, int green, int blue) =>
        red >= 210 && green >= 210 && blue >= 210;

    private static bool IsLime(int red, int green, int blue) =>
        red >= 120 && green >= 190 && blue <= 90 && green >= red;

    private static bool IsDark(int red, int green, int blue) =>
        red <= 85 && green <= 85 && blue <= 85;

    private static Rectangle ToPixels(Bitmap source, NormalizedRegion region)
    {
        var left = Math.Clamp((int)Math.Round(source.Width * region.Left), 0, source.Width - 1);
        var top = Math.Clamp((int)Math.Round(source.Height * region.Top), 0, source.Height - 1);
        var right = Math.Clamp((int)Math.Round(source.Width * region.Right), left + 1, source.Width);
        var bottom = Math.Clamp((int)Math.Round(source.Height * region.Bottom), top + 1, source.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private readonly record struct NormalizedRegion(double Left, double Top, double Right, double Bottom);
}
