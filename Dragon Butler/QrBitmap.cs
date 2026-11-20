using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DragonButler;

/// <summary>
/// 把 <see cref="QrCode"/> 出来的矩阵画成一张图（v1.31）。
///
/// 单独一个文件、只依赖 WPF 的 Media：QrCode 本身是纯算法，不碰 UI，
/// 这样探针可以把它拷到控制台工程里逐格断言（真那么干过，见路线图 v1.31）。
/// </summary>
public static class QrBitmap
{
    /// <summary>
    /// 每个模块放大 <paramref name="scale"/> 像素，四周留 <paramref name="quiet"/> 个模块的白边。
    /// 白边不是装饰——没有它读码器找不到二维码的边界，扫不出来。
    /// </summary>
    public static BitmapSource? Render(string text, int scale = 6, int quiet = 4)
    {
        var matrix = QrCode.Encode(text);
        if (matrix == null) return null;

        int size = matrix.GetLength(0);
        int pixels = (size + quiet * 2) * scale;

        // 白底黑块。用 Bgra32 手工填，不引入任何绘图库。
        int stride = pixels * 4;
        var buffer = new byte[stride * pixels];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = 0xFF;      // B
            buffer[i + 1] = 0xFF;  // G
            buffer[i + 2] = 0xFF;  // R
            buffer[i + 3] = 0xFF;  // A
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!matrix[y, x]) continue;
                int py0 = (y + quiet) * scale;
                int px0 = (x + quiet) * scale;
                for (int dy = 0; dy < scale; dy++)
                {
                    int row = (py0 + dy) * stride;
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int idx = row + (px0 + dx) * 4;
                        buffer[idx] = 0x00;
                        buffer[idx + 1] = 0x00;
                        buffer[idx + 2] = 0x00;
                    }
                }
            }
        }

        var bmp = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        bmp.Freeze();
        return bmp;
    }
}
