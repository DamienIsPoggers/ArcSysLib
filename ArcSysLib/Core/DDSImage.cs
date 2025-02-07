﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using VFSILib.Common.Enum;
using VFSILib.Core.IO;

namespace ArcSysLib.Core;

public class DDSImage : IDisposable
{
    private const int DDSD_CAPS = 0x00000001;
    private const int DDSD_HEIGHT = 0x00000002;
    private const int DDSD_WIDTH = 0x00000004;
    private const int DDSD_PITCH = 0x00000008;
    private const int DDSD_PIXELFORMAT = 0x00001000;
    private const int DDSD_MIPMAPCOUNT = 0x00020000;
    private const int DDSD_LINEARSIZE = 0x00080000;
    private const int DDSD_DEPTH = 0x00800000;


    private const int DDPF_ALPHAPIXELS = 0x00000001;
    private const int DDPF_FOURCC = 0x00000004;
    private const int DDPF_RGB = 0x00000040;
    private const int DDPF_LUMINANCE = 0x00020000;


    // caps1
    private const int DDSCAPS_COMPLEX = 0x00000008;
    private const int DDSCAPS_TEXTURE = 0x00001000;

    private const int DDSCAPS_MIPMAP = 0x00400000;

    // caps2
    private const int DDSCAPS2_CUBEMAP = 0x00000200;
    private const int DDSCAPS2_CUBEMAP_POSITIVEX = 0x00000400;
    private const int DDSCAPS2_CUBEMAP_NEGATIVEX = 0x00000800;
    private const int DDSCAPS2_CUBEMAP_POSITIVEY = 0x00001000;
    private const int DDSCAPS2_CUBEMAP_NEGATIVEY = 0x00002000;
    private const int DDSCAPS2_CUBEMAP_POSITIVEZ = 0x00004000;
    private const int DDSCAPS2_CUBEMAP_NEGATIVEZ = 0x00008000;
    private const int DDSCAPS2_VOLUME = 0x00200000;


    private const uint FOURCC_DXT1 = 0x31545844;
    private const uint FOURCC_DXT2 = 0x32545844;
    private const uint FOURCC_DXT3 = 0x33545844;
    private const uint FOURCC_DXT4 = 0x34545844;
    private const uint FOURCC_DXT5 = 0x35545844;
    private const uint FOURCC_ATI1 = 0x31495441;
    private const uint FOURCC_ATI2 = 0x32495441;
    private const uint FOURCC_RXGB = 0x42475852;
    private const uint FOURCC_DOLLARNULL = 0x24;
    private const uint FOURCC_oNULL = 0x6f;
    private const uint FOURCC_pNULL = 0x70;
    private const uint FOURCC_qNULL = 0x71;
    private const uint FOURCC_rNULL = 0x72;
    private const uint FOURCC_sNULL = 0x73;
    private const uint FOURCC_tNULL = 0x74;

    public DDSImage(byte[] ddsImage, ByteOrder endianness = ByteOrder.LittleEndian)
    {
        if (ddsImage == null) return;
        if (ddsImage.Length == 0) return;

        using var stream = new MemoryStream(ddsImage.Length);
        stream.Write(ddsImage, 0, ddsImage.Length);
        stream.Seek(0, SeekOrigin.Begin);

        using var reader = new EndiannessAwareBinaryReader(stream, endianness);
        Parse(reader);
    }

    public DDSImage(Stream ddsImage)
    {
        if (ddsImage == null) return;
        if (!ddsImage.CanRead) return;

        using var reader = new EndiannessAwareBinaryReader(ddsImage);
        Parse(reader);
    }

    private DDSImage(Bitmap bmpsrc)
    {
        BitmapImage = bmpsrc;
    }

    /// <summary>
    ///     Returns a System.Imaging.Bitmap containing the DDS image.
    /// </summary>
    public Bitmap BitmapImage { get; private set; }

    /// <summary>
    ///     Returns the DDS image is valid format.
    /// </summary>
    public bool IsValid { get; private set; }

    public void Dispose()
    {
        if (BitmapImage != null)
        {
            BitmapImage.Dispose();
            BitmapImage = null;
        }
    }

    private void Parse(EndiannessAwareBinaryReader reader)
    {
        var header = new DDSStruct();
        if (ReadHeader(reader, ref header))
        {
            IsValid = true;
            // patches for stuff
            if (header.depth == 0) header.depth = 1;

            uint blocksize = 0;
            var pixelFormat = GetFormat(header, ref blocksize);
            if (pixelFormat == PixelFormat.UNKNOWN)
                throw new InvalidFileHeaderException();

            reader.ChangeEndianness(ByteOrder.LittleEndian);
            var data = ReadData(reader, header);
            if (data != null)
            {
                var rawData = DecompressData(header, data, pixelFormat);
                BitmapImage = CreateBitmap((int) header.width, (int) header.height, rawData);
            }
        }
    }

    private byte[] ReadData(BinaryReader reader, DDSStruct header)
    {
        byte[] compdata;
        uint compsize;
        if ((header.flags & DDSD_LINEARSIZE) > 1)
        {
            compdata = reader.ReadBytes((int) header.sizeorpitch);
            //compsize = (uint)compdata.Length;
        }
        else
        {
            var bps = header.width * header.pixelformat.rgbbitcount / 8;
            compsize = bps * header.height * header.depth;
            compdata = new byte[compsize];

            var mem = new MemoryStream((int) compsize);

            byte[] temp;
            for (var z = 0; z < header.depth; z++)
            for (var y = 0; y < header.height; y++)
            {
                temp = reader.ReadBytes((int) bps);
                mem.Write(temp, 0, temp.Length);
            }

            mem.Seek(0, SeekOrigin.Begin);

            mem.Read(compdata, 0, compdata.Length);
            mem.Close();
        }

        return compdata;
    }

    private Bitmap CreateBitmap(int width, int height, byte[] rawData)
    {
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height)
            , ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var scan = data.Scan0;
        var size = bitmap.Width * bitmap.Height * 4;

        unsafe
        {
            var p = (byte*) scan;
            for (var i = 0; i < size; i += 4)
            {
                // iterate through bytes.
                // Bitmap stores it's data in RGBA order.
                // DDS stores it's data in BGRA order.
                p[i] = rawData[i + 2]; // blue
                p[i + 1] = rawData[i + 1]; // green
                p[i + 2] = rawData[i]; // red
                p[i + 3] = rawData[i + 3]; // alpha
            }
        }

        bitmap.UnlockBits(data);
        return bitmap;
    }

    private bool ReadHeader(BinaryReader reader, ref DDSStruct header)
    {
        var signature = reader.ReadBytes(4);
        var signatureText = Encoding.ASCII.GetString(signature);
        var signatureInt = BitConverter.ToInt32(signature, 0);

        if (signatureText == "DDS ")
        {
            header.size = reader.ReadUInt32();
            if (header.size != 124)
                return false;

            //convert the data
            header.flags = reader.ReadUInt32();
            header.height = reader.ReadUInt32();
            header.width = reader.ReadUInt32();
            header.sizeorpitch = reader.ReadUInt32();
            header.depth = reader.ReadUInt32();
            header.mipmapcount = reader.ReadUInt32();
            header.alphabitdepth = reader.ReadUInt32();

            header.reserved = new uint[10];
            for (var i = 0; i < 10; i++) header.reserved[i] = reader.ReadUInt32();

            //pixelformat
            header.pixelformat.size = reader.ReadUInt32();
            header.pixelformat.flags = reader.ReadUInt32();
            header.pixelformat.fourcc = reader.ReadUInt32();
            header.pixelformat.rgbbitcount = reader.ReadUInt32();
            if (header.pixelformat.rgbbitcount == 0)
                header.pixelformat.rgbbitcount = 32;
            header.pixelformat.rbitmask = reader.ReadUInt32();
            if (header.pixelformat.rbitmask == 0)
                header.pixelformat.rbitmask = 0x00ff0000;
            header.pixelformat.gbitmask = reader.ReadUInt32();
            if (header.pixelformat.gbitmask == 0)
                header.pixelformat.gbitmask = 0x0000ff00;
            header.pixelformat.bbitmask = reader.ReadUInt32();
            if (header.pixelformat.bbitmask == 0)
                header.pixelformat.bbitmask = 0x000000ff;
            header.pixelformat.alphabitmask = reader.ReadUInt32();
            if (header.pixelformat.alphabitmask == 0)
                header.pixelformat.alphabitmask = 0xff000000;

            //caps
            header.ddscaps.caps1 = reader.ReadUInt32();
            header.ddscaps.caps2 = reader.ReadUInt32();
            header.ddscaps.caps3 = reader.ReadUInt32();
            header.ddscaps.caps4 = reader.ReadUInt32();
            header.texturestage = reader.ReadUInt32();
        }
        else if (signatureText == "XPR2")
        {
        }
        else if (signatureInt == 0x01080000)
        {
            header.sizeorpitch = reader.ReadUInt32();
            header.depth = reader.ReadUInt32();
            reader.BaseStream.Seek(8, SeekOrigin.Current);
            header.ddscaps.caps1 = reader.ReadUInt32();
            header.flags = reader.ReadUInt32();
            reader.BaseStream.Seek(4, SeekOrigin.Current);
            header.width = reader.ReadUInt16();
            header.height = reader.ReadUInt16();
            reader.BaseStream.Seek(92, SeekOrigin.Current);

            header.pixelformat.size = 32;
            header.pixelformat.flags = 4;
            header.pixelformat.rgbbitcount = 32;
            header.pixelformat.rbitmask = 0x00ff0000;
            header.pixelformat.gbitmask = 0x0000ff00;
            header.pixelformat.bbitmask = 0x000000ff;
            header.pixelformat.alphabitmask = 0xff000000;
        }
        else
        {
            return false;
        }

        return true;
    }

    private PixelFormat GetFormat(DDSStruct header, ref uint blocksize)
    {
        PixelFormat format;
        if ((header.pixelformat.flags & DDPF_FOURCC) == DDPF_FOURCC)
        {
            blocksize = (header.width + 3) / 4 * ((header.height + 3) / 4) * header.depth;

            switch (header.pixelformat.fourcc)
            {
                case FOURCC_DXT1:
                    format = PixelFormat.DXT1;
                    blocksize *= 8;
                    break;

                case FOURCC_DXT2:
                    format = PixelFormat.DXT2;
                    blocksize *= 16;
                    break;

                case FOURCC_DXT3:
                    format = PixelFormat.DXT3;
                    blocksize *= 16;
                    break;

                case FOURCC_DXT4:
                    format = PixelFormat.DXT4;
                    blocksize *= 16;
                    break;

                case FOURCC_DXT5:
                    format = PixelFormat.DXT5;
                    blocksize *= 16;
                    break;

                case FOURCC_ATI1:
                    format = PixelFormat.ATI1N;
                    blocksize *= 8;
                    break;

                case FOURCC_ATI2:
                    format = PixelFormat.THREEDC;
                    blocksize *= 16;
                    break;

                case FOURCC_RXGB:
                    format = PixelFormat.RXGB;
                    blocksize *= 16;
                    break;

                case FOURCC_DOLLARNULL:
                    format = PixelFormat.A16B16G16R16;
                    blocksize = header.width * header.height * header.depth * 8;
                    break;

                case FOURCC_oNULL:
                    format = PixelFormat.R16F;
                    blocksize = header.width * header.height * header.depth * 2;
                    break;

                case FOURCC_pNULL:
                    format = PixelFormat.G16R16F;
                    blocksize = header.width * header.height * header.depth * 4;
                    break;

                case FOURCC_qNULL:
                    format = PixelFormat.A16B16G16R16F;
                    blocksize = header.width * header.height * header.depth * 8;
                    break;

                case FOURCC_rNULL:
                    format = PixelFormat.R32F;
                    blocksize = header.width * header.height * header.depth * 4;
                    break;

                case FOURCC_sNULL:
                    format = PixelFormat.G32R32F;
                    blocksize = header.width * header.height * header.depth * 8;
                    break;

                case FOURCC_tNULL:
                    format = PixelFormat.A32B32G32R32F;
                    blocksize = header.width * header.height * header.depth * 16;
                    break;

                default:
                    format = PixelFormat.UNKNOWN;
                    blocksize *= 16;
                    break;
            } // switch

            if (format == PixelFormat.UNKNOWN)
            {
                if ((header.flags & 0x8000000) == 0x8000000)
                    format = PixelFormat.DXT5;
                else if ((header.flags & 0x6000000) == 0x6000000)
                    format = PixelFormat.DXT1;
            }
        }
        else
        {
            // uncompressed image
            if ((header.pixelformat.flags & DDPF_LUMINANCE) == DDPF_LUMINANCE)
            {
                if ((header.pixelformat.flags & DDPF_ALPHAPIXELS) == DDPF_ALPHAPIXELS)
                    format = PixelFormat.LUMINANCE_ALPHA;
                else
                    format = PixelFormat.LUMINANCE;
            }
            else
            {
                if ((header.pixelformat.flags & DDPF_ALPHAPIXELS) == DDPF_ALPHAPIXELS)
                    format = PixelFormat.RGBA;
                else
                    format = PixelFormat.RGB;
            }

            blocksize = header.width * header.height * header.depth * (header.pixelformat.rgbbitcount >> 3);
        }

        return format;
    }

    // iCompFormatToBpp
    private uint PixelFormatToBpp(PixelFormat pf, uint rgbbitcount)
    {
        return pf switch
        {
            PixelFormat.LUMINANCE or PixelFormat.LUMINANCE_ALPHA or PixelFormat.RGBA or PixelFormat.RGB => rgbbitcount /
                8,
            PixelFormat.THREEDC or PixelFormat.RXGB => 3,
            PixelFormat.ATI1N => 1,
            PixelFormat.R16F => 2,
            PixelFormat.A16B16G16R16 or PixelFormat.A16B16G16R16F or PixelFormat.G32R32F => 8,
            PixelFormat.A32B32G32R32F => 16,
            _ => 4
        };
    }

    // iCompFormatToBpc
    private uint PixelFormatToBpc(PixelFormat pf)
    {
        return pf switch
        {
            PixelFormat.R16F or PixelFormat.G16R16F or PixelFormat.A16B16G16R16F => 4,
            PixelFormat.R32F or PixelFormat.G32R32F or PixelFormat.A32B32G32R32F => 4,
            PixelFormat.A16B16G16R16 => 2,
            _ => 1
        };
    }

    private bool Check16BitComponents(DDSStruct header)
    {
        if (header.pixelformat.rgbbitcount != 32)
            return false;
        // a2b10g10r10 format
        if (header.pixelformat.rbitmask == 0x3FF00000 && header.pixelformat.gbitmask == 0x000FFC00 &&
            header.pixelformat.bbitmask == 0x000003FF
            && header.pixelformat.alphabitmask == 0xC0000000)
            return true;
        // a2r10g10b10 format
        if (header.pixelformat.rbitmask == 0x000003FF && header.pixelformat.gbitmask == 0x000FFC00 &&
            header.pixelformat.bbitmask == 0x3FF00000
            && header.pixelformat.alphabitmask == 0xC0000000)
            return true;

        return false;
    }

    private void CorrectPremult(uint pixnum, ref byte[] buffer)
    {
        for (uint i = 0; i < pixnum; i++)
        {
            var alpha = buffer[i + 3];
            if (alpha == 0) continue;
            var red = (buffer[i] << 8) / alpha;
            var green = (buffer[i + 1] << 8) / alpha;
            var blue = (buffer[i + 2] << 8) / alpha;

            buffer[i] = (byte) red;
            buffer[i + 1] = (byte) green;
            buffer[i + 2] = (byte) blue;
        }
    }

    private void ComputeMaskParams(uint mask, ref int shift1, ref int mul, ref int shift2)
    {
        shift1 = 0;
        mul = 1;
        shift2 = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift1++;
        }

        uint bc = 0;
        while ((mask & (1 << (int) bc)) != 0) bc++;
        while (mask * mul < 255)
            mul = (mul << (int) bc) + 1;
        mask *= (uint) mul;

        while ((mask & ~0xff) != 0)
        {
            mask >>= 1;
            shift2++;
        }
    }

    private unsafe void DxtcReadColors(byte* data, ref Colour8888[] op)
    {
        byte r0, g0, b0, r1, g1, b1;

        b0 = (byte) (data[0] & 0x1F);
        g0 = (byte) (((data[0] & 0xE0) >> 5) | ((data[1] & 0x7) << 3));
        r0 = (byte) ((data[1] & 0xF8) >> 3);

        b1 = (byte) (data[2] & 0x1F);
        g1 = (byte) (((data[2] & 0xE0) >> 5) | ((data[3] & 0x7) << 3));
        r1 = (byte) ((data[3] & 0xF8) >> 3);

        op[0].red = (byte) ((r0 << 3) | (r0 >> 2));
        op[0].green = (byte) ((g0 << 2) | (g0 >> 3));
        op[0].blue = (byte) ((b0 << 3) | (b0 >> 2));

        op[1].red = (byte) ((r1 << 3) | (r1 >> 2));
        op[1].green = (byte) ((g1 << 2) | (g1 >> 3));
        op[1].blue = (byte) ((b1 << 3) | (b1 >> 2));
    }

    private void DxtcReadColor(ushort data, ref Colour8888 op)
    {
        byte r, g, b;

        b = (byte) (data & 0x1f);
        g = (byte) ((data & 0x7E0) >> 5);
        r = (byte) ((data & 0xF800) >> 11);

        op.red = (byte) ((r << 3) | (r >> 2));
        op.green = (byte) ((g << 2) | (g >> 3));
        op.blue = (byte) ((b << 3) | (r >> 2));
    }

    private unsafe void DxtcReadColors(byte* data, ref Colour565 color_0, ref Colour565 color_1)
    {
        color_0.blue = (byte) (data[0] & 0x1F);
        color_0.green = (byte) (((data[0] & 0xE0) >> 5) | ((data[1] & 0x7) << 3));
        color_0.red = (byte) ((data[1] & 0xF8) >> 3);

        color_0.blue = (byte) (data[2] & 0x1F);
        color_0.green = (byte) (((data[2] & 0xE0) >> 5) | ((data[3] & 0x7) << 3));
        color_0.red = (byte) ((data[3] & 0xF8) >> 3);
    }

    private void GetBitsFromMask(uint mask, ref uint shiftLeft, ref uint shiftRight)
    {
        uint temp, i;

        if (mask == 0)
        {
            shiftLeft = shiftRight = 0;
            return;
        }

        temp = mask;
        for (i = 0; i < 32; i++, temp >>= 1)
            if ((temp & 1) != 0)
                break;
        shiftRight = i;

        // Temp is preserved, so use it again:
        for (i = 0; i < 8; i++, temp >>= 1)
            if ((temp & 1) == 0)
                break;
        shiftLeft = 8 - i;
    }

    // This function simply counts how many contiguous bits are in the mask.
    private uint CountBitsFromMask(uint mask)
    {
        uint i, testBit = 0x01, count = 0;
        var foundBit = false;

        for (i = 0; i < 32; i++, testBit <<= 1)
            if ((mask & testBit) != 0)
            {
                if (!foundBit)
                    foundBit = true;
                count++;
            }
            else if (foundBit)
            {
                return count;
            }

        return count;
    }

    private uint HalfToFloat(ushort y)
    {
        var s = (y >> 15) & 0x00000001;
        var e = (y >> 10) & 0x0000001f;
        var m = y & 0x000003ff;

        if (e == 0)
        {
            if (m == 0)
                //
                // Plus or minus zero
                //
                return (uint) (s << 31);

            //
            // Denormalized number -- renormalize it
            //
            while ((m & 0x00000400) == 0)
            {
                m <<= 1;
                e -= 1;
            }

            e += 1;
            m &= ~0x00000400;
        }
        else if (e == 31)
        {
            if (m == 0)
                //
                // Positive or negative infinity
                //
                return (uint) ((s << 31) | 0x7f800000);
            return (uint) ((s << 31) | 0x7f800000 | (m << 13));
        }

        //
        // Normalized number
        //
        e += 127 - 15;
        m <<= 13;

        //
        // Assemble s, e and m.
        //
        return (uint) ((s << 31) | (e << 23) | m);
    }

    private unsafe void ConvFloat16ToFloat32(uint* dest, ushort* src, uint size)
    {
        uint i;
        for (i = 0; i < size; ++i, ++dest, ++src)
            //float: 1 sign bit, 8 exponent bits, 23 mantissa bits
            //half: 1 sign bit, 5 exponent bits, 10 mantissa bits
            *dest = HalfToFloat(*src);
    }

    private unsafe void ConvG16R16ToFloat32(uint* dest, ushort* src, uint size)
    {
        uint i;
        for (i = 0; i < size; i += 3)
        {
            //float: 1 sign bit, 8 exponent bits, 23 mantissa bits
            //half: 1 sign bit, 5 exponent bits, 10 mantissa bits
            *dest++ = HalfToFloat(*src++);
            *dest++ = HalfToFloat(*src++);
            *(float*) dest++ = 1.0f;
        }
    }

    private unsafe void ConvR16ToFloat32(uint* dest, ushort* src, uint size)
    {
        uint i;
        for (i = 0; i < size; i += 3)
        {
            //float: 1 sign bit, 8 exponent bits, 23 mantissa bits
            //half: 1 sign bit, 5 exponent bits, 10 mantissa bits
            *dest++ = HalfToFloat(*src++);
            *(float*) dest++ = 1.0f;
            *(float*) dest++ = 1.0f;
        }
    }

    private byte[] DecompressData(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        Debug.WriteLine(pixelFormat);
        var rawData = pixelFormat switch
        {
            PixelFormat.RGBA => DecompressRGBA(header, data, pixelFormat),
            PixelFormat.RGB => DecompressRGB(header, data, pixelFormat),
            PixelFormat.LUMINANCE or PixelFormat.LUMINANCE_ALPHA => DecompressLum(header, data, pixelFormat),
            PixelFormat.DXT1 => DecompressDXT1(header, data, pixelFormat),
            PixelFormat.DXT2 => DecompressDXT2(header, data, pixelFormat),
            PixelFormat.DXT3 => DecompressDXT3(header, data, pixelFormat),
            PixelFormat.DXT4 => DecompressDXT4(header, data, pixelFormat),
            PixelFormat.DXT5 => DecompressDXT5(header, data, pixelFormat),
            PixelFormat.THREEDC => Decompress3Dc(header, data, pixelFormat),
            PixelFormat.ATI1N => DecompressAti1n(header, data, pixelFormat),
            PixelFormat.RXGB => DecompressRXGB(header, data, pixelFormat),
            PixelFormat.R16F or PixelFormat.G16R16F or PixelFormat.A16B16G16R16F or PixelFormat.R32F
                or PixelFormat.G32R32F or PixelFormat.A32B32G32R32F => DecompressFloat(header, data, pixelFormat),
            _ => throw new UnknownFileFormatException()
        };
        return rawData;
    }

    private unsafe byte[] DecompressDXT1(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        // DXT1 decompressor
        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];

        var colors = new Colour8888[4];
        colors[0].alpha = 0xFF;
        colors[1].alpha = 0xFF;
        colors[2].alpha = 0xFF;

        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y += 4)
            for (var x = 0; x < width; x += 4)
            {
                var colour0 = *(ushort*) temp;
                var colour1 = *(ushort*) (temp + 2);
                DxtcReadColor(colour0, ref colors[0]);
                DxtcReadColor(colour1, ref colors[1]);

                var bitmask = ((uint*) temp)[1];
                temp += 8;

                if (colour0 > colour1)
                {
                    // Four-color block: derive the other two colors.
                    // 00 = color_0, 01 = color_1, 10 = color_2, 11 = color_3
                    // These 2-bit codes correspond to the 2-bit fields
                    // stored in the 64-bit block.
                    colors[2].blue = (byte) ((2 * colors[0].blue + colors[1].blue + 1) / 3);
                    colors[2].green = (byte) ((2 * colors[0].green + colors[1].green + 1) / 3);
                    colors[2].red = (byte) ((2 * colors[0].red + colors[1].red + 1) / 3);
                    //colors[2].alpha = 0xFF;

                    colors[3].blue = (byte) ((colors[0].blue + 2 * colors[1].blue + 1) / 3);
                    colors[3].green = (byte) ((colors[0].green + 2 * colors[1].green + 1) / 3);
                    colors[3].red = (byte) ((colors[0].red + 2 * colors[1].red + 1) / 3);
                    colors[3].alpha = 0xFF;
                }
                else
                {
                    // Three-color block: derive the other color.
                    // 00 = color_0,  01 = color_1,  10 = color_2,
                    // 11 = transparent.
                    // These 2-bit codes correspond to the 2-bit fields 
                    // stored in the 64-bit block. 
                    colors[2].blue = (byte) ((colors[0].blue + colors[1].blue) / 2);
                    colors[2].green = (byte) ((colors[0].green + colors[1].green) / 2);
                    colors[2].red = (byte) ((colors[0].red + colors[1].red) / 2);
                    //colors[2].alpha = 0xFF;

                    colors[3].blue = (byte) ((colors[0].blue + 2 * colors[1].blue + 1) / 3);
                    colors[3].green = (byte) ((colors[0].green + 2 * colors[1].green + 1) / 3);
                    colors[3].red = (byte) ((colors[0].red + 2 * colors[1].red + 1) / 3);
                    colors[3].alpha = 0x00;
                }

                for (int j = 0, k = 0; j < 4; j++)
                for (var i = 0; i < 4; i++, k++)
                {
                    var select = (int) ((bitmask & (0x03 << (k * 2))) >> (k * 2));
                    var col = colors[select];
                    if (x + i < width && y + j < height)
                    {
                        var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp);
                        rawData[offset + 0] = col.red;
                        rawData[offset + 1] = col.green;
                        rawData[offset + 2] = col.blue;
                        rawData[offset + 3] = col.alpha;
                    }
                }
            }
        }

        return rawData;
    }

    private byte[] DecompressDXT2(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        // Can do color & alpha same as dxt3, but color is pre-multiplied
        // so the result will be wrong unless corrected.
        var rawData = DecompressDXT3(header, data, pixelFormat);
        CorrectPremult((uint) (width * height * depth), ref rawData);

        return rawData;
    }

    private unsafe byte[] DecompressDXT3(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        // DXT3 decompressor
        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];
        var colors = new Colour8888[4];

        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y += 4)
            for (var x = 0; x < width; x += 4)
            {
                var alpha = temp;
                temp += 8;

                DxtcReadColors(temp, ref colors);
                temp += 4;

                var bitmask = ((uint*) temp)[1];
                temp += 4;

                // Four-color block: derive the other two colors.
                // 00 = color_0, 01 = color_1, 10 = color_2, 11	= color_3
                // These 2-bit codes correspond to the 2-bit fields
                // stored in the 64-bit block.
                colors[2].blue = (byte) ((2 * colors[0].blue + colors[1].blue) / 3);
                colors[2].green = (byte) ((2 * colors[0].green + colors[1].green) / 3);
                colors[2].red = (byte) ((2 * colors[0].red + colors[1].red) / 3);
                //colors[2].alpha = 0xFF;

                colors[3].blue = (byte) ((colors[0].blue + 2 * colors[1].blue + 1) / 3);
                colors[3].green = (byte) ((colors[0].green + 2 * colors[1].green + 1) / 3);
                colors[3].red = (byte) ((colors[0].red + 2 * colors[1].red + 1) / 3);
                //colors[3].alpha = 0xFF;

                if (colors[0].blue == colors[0].green && colors[0].green == colors[0].red &&
                    colors[0].blue == 0xFF &&
                    colors[1].blue == colors[1].green && colors[1].green == colors[1].red && colors[1].blue == 0x0)
                    colors[2].blue = colors[2].green =
                        colors[2].red = colors[3].blue = colors[3].green = colors[3].red = 0xFF;

                for (int j = 0, k = 0; j < 4; j++)
                for (var i = 0; i < 4; k++, i++)
                {
                    var select = (int) ((bitmask & (0x03 << (k * 2))) >> (k * 2));

                    if (x + i < width && y + j < height)
                    {
                        var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp);
                        rawData[offset + 0] = colors[select].red;
                        rawData[offset + 1] = colors[select].green;
                        rawData[offset + 2] = colors[select].blue;
                    }
                }

                for (var j = 0; j < 4; j++)
                {
                    //ushort word = (ushort)(alpha[2 * j] + 256 * alpha[2 * j + 1]);
                    var word = (ushort) (alpha[2 * j] | (alpha[2 * j + 1] << 8));
                    for (var i = 0; i < 4; i++)
                    {
                        if (x + i < width && y + j < height)
                        {
                            var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp + 3);
                            rawData[offset] = (byte) (word & 0x0F);
                            rawData[offset] = (byte) (rawData[offset] | (rawData[offset] << 4));
                        }

                        word >>= 4;
                    }
                }
            }
        }

        return rawData;
    }

    private byte[] DecompressDXT4(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        // Can do color & alpha same as dxt5, but color is pre-multiplied
        // so the result will be wrong unless corrected.
        var rawData = DecompressDXT5(header, data, pixelFormat);
        CorrectPremult((uint) (width * height * depth), ref rawData);

        return rawData;
    }

    private unsafe byte[] DecompressDXT5(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];
        var colors = new Colour8888[4];
        var alphas = new ushort[8];

        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y += 4)
            for (var x = 0; x < width; x += 4)
            {
                if (y >= height || x >= width)
                    break;

                alphas[0] = temp[0];
                alphas[1] = temp[1];
                var alphamask = temp + 2;
                temp += 8;

                DxtcReadColors(temp, ref colors);
                var bitmask = ((uint*) temp)[1];
                temp += 8;

                // Four-color block: derive the other two colors.
                // 00 = color_0, 01 = color_1, 10 = color_2, 11	= color_3
                // These 2-bit codes correspond to the 2-bit fields
                // stored in the 64-bit block.
                colors[2].blue = (byte) ((2 * colors[0].blue + colors[1].blue + 1) / 3);
                colors[2].green = (byte) ((2 * colors[0].green + colors[1].green + 1) / 3);
                colors[2].red = (byte) ((2 * colors[0].red + colors[1].red + 1) / 3);
                //colors[2].alpha = 0xFF;

                colors[3].blue = (byte) ((colors[0].blue + 2 * colors[1].blue + 1) / 3);
                colors[3].green = (byte) ((colors[0].green + 2 * colors[1].green + 1) / 3);
                colors[3].red = (byte) ((colors[0].red + 2 * colors[1].red + 1) / 3);
                //colors[3].alpha = 0xFF;

                var k = 0;
                for (var j = 0; j < 4; j++)
                for (var i = 0; i < 4; k++, i++)
                {
                    var select = (int) ((bitmask & (0x03 << (k * 2))) >> (k * 2));
                    var col = colors[select];
                    // only put pixels out < width or height
                    if (x + i < width && y + j < height)
                    {
                        var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp);
                        rawData[offset] = col.red;
                        rawData[offset + 1] = col.green;
                        rawData[offset + 2] = col.blue;
                    }
                }

                // 8-alpha or 6-alpha block?
                if (alphas[0] > alphas[1])
                {
                    // 8-alpha block:  derive the other six alphas.
                    // Bit code 000 = alpha_0, 001 = alpha_1, others are interpolated.
                    alphas[2] = (ushort) ((6 * alphas[0] + 1 * alphas[1] + 3) / 7); // bit code 010
                    alphas[3] = (ushort) ((5 * alphas[0] + 2 * alphas[1] + 3) / 7); // bit code 011
                    alphas[4] = (ushort) ((4 * alphas[0] + 3 * alphas[1] + 3) / 7); // bit code 100
                    alphas[5] = (ushort) ((3 * alphas[0] + 4 * alphas[1] + 3) / 7); // bit code 101
                    alphas[6] = (ushort) ((2 * alphas[0] + 5 * alphas[1] + 3) / 7); // bit code 110
                    alphas[7] = (ushort) ((1 * alphas[0] + 6 * alphas[1] + 3) / 7); // bit code 111
                }
                else
                {
                    // 6-alpha block.
                    // Bit code 000 = alpha_0, 001 = alpha_1, others are interpolated.
                    alphas[2] = (ushort) ((4 * alphas[0] + 1 * alphas[1] + 2) / 5); // Bit code 010
                    alphas[3] = (ushort) ((3 * alphas[0] + 2 * alphas[1] + 2) / 5); // Bit code 011
                    alphas[4] = (ushort) ((2 * alphas[0] + 3 * alphas[1] + 2) / 5); // Bit code 100
                    alphas[5] = (ushort) ((1 * alphas[0] + 4 * alphas[1] + 2) / 5); // Bit code 101
                    alphas[6] = 0x00; // Bit code 110
                    alphas[7] = 0xFF; // Bit code 111
                }

                // Note: Have to separate the next two loops,
                // it operates on a 6-byte system.

                // First three bytes
                //uint bits = (uint)(alphamask[0]);
                var bits = (uint) (alphamask[0] | (alphamask[1] << 8) | (alphamask[2] << 16));
                for (var j = 0; j < 2; j++)
                for (var i = 0; i < 4; i++)
                {
                    // only put pixels out < width or height
                    if (x + i < width && y + j < height)
                    {
                        var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp + 3);
                        rawData[offset] = (byte) alphas[bits & 0x07];
                    }

                    bits >>= 3;
                }

                // Last three bytes
                //bits = (uint)(alphamask[3]);
                bits = (uint) (alphamask[3] | (alphamask[4] << 8) | (alphamask[5] << 16));
                for (var j = 2; j < 4; j++)
                for (var i = 0; i < 4; i++)
                {
                    // only put pixels out < width or height
                    if (x + i < width && y + j < height)
                    {
                        var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp + 3);
                        rawData[offset] = (byte) alphas[bits & 0x07];
                    }

                    bits >>= 3;
                }
            }
        }

        return rawData;
    }

    private unsafe byte[] DecompressRGB(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];

        var valMask = (uint) (header.pixelformat.rgbbitcount == 32
            ? ~0
            : (1 << (int) header.pixelformat.rgbbitcount) - 1);
        var pixSize = (uint) (((int) header.pixelformat.rgbbitcount + 7) / 8);
        var rShift1 = 0;
        var rMul = 0;
        var rShift2 = 0;
        ComputeMaskParams(header.pixelformat.rbitmask, ref rShift1, ref rMul, ref rShift2);
        var gShift1 = 0;
        var gMul = 0;
        var gShift2 = 0;
        ComputeMaskParams(header.pixelformat.gbitmask, ref gShift1, ref gMul, ref gShift2);
        var bShift1 = 0;
        var bMul = 0;
        var bShift2 = 0;
        ComputeMaskParams(header.pixelformat.bbitmask, ref bShift1, ref bMul, ref bShift2);

        var offset = 0;
        var pixnum = width * height * depth;
        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            while (pixnum-- > 0)
            {
                var px = *(uint*) temp & valMask;
                temp += pixSize;
                var pxc = px & header.pixelformat.rbitmask;
                rawData[offset + 0] = (byte) (((pxc >> rShift1) * rMul) >> rShift2);
                pxc = px & header.pixelformat.gbitmask;
                rawData[offset + 1] = (byte) (((pxc >> gShift1) * gMul) >> gShift2);
                pxc = px & header.pixelformat.bbitmask;
                rawData[offset + 2] = (byte) (((pxc >> bShift1) * bMul) >> bShift2);
                rawData[offset + 3] = 0xff;
                offset += 4;
            }
        }

        return rawData;
    }

    private unsafe byte[] DecompressRGBA(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];

        var valMask = (uint) (header.pixelformat.rgbbitcount == 32
            ? ~0
            : (1 << (int) header.pixelformat.rgbbitcount) - 1);
        // Funny x86s, make 1 << 32 == 1
        var pixSize = (header.pixelformat.rgbbitcount + 7) / 8;
        var rShift1 = 0;
        var rMul = 0;
        var rShift2 = 0;
        ComputeMaskParams(header.pixelformat.rbitmask, ref rShift1, ref rMul, ref rShift2);
        var gShift1 = 0;
        var gMul = 0;
        var gShift2 = 0;
        ComputeMaskParams(header.pixelformat.gbitmask, ref gShift1, ref gMul, ref gShift2);
        var bShift1 = 0;
        var bMul = 0;
        var bShift2 = 0;
        ComputeMaskParams(header.pixelformat.bbitmask, ref bShift1, ref bMul, ref bShift2);
        var aShift1 = 0;
        var aMul = 0;
        var aShift2 = 0;
        ComputeMaskParams(header.pixelformat.alphabitmask, ref aShift1, ref aMul, ref aShift2);

        var offset = 0;
        var pixnum = width * height * depth;
        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;

            while (pixnum-- > 0)
            {
                var px = *(uint*) temp & valMask;
                temp += pixSize;
                var pxc = px & header.pixelformat.rbitmask;
                rawData[offset + 0] = (byte) (((pxc >> rShift1) * rMul) >> rShift2);
                pxc = px & header.pixelformat.gbitmask;
                rawData[offset + 1] = (byte) (((pxc >> gShift1) * gMul) >> gShift2);
                pxc = px & header.pixelformat.bbitmask;
                rawData[offset + 2] = (byte) (((pxc >> bShift1) * bMul) >> bShift2);
                pxc = px & header.pixelformat.alphabitmask;
                rawData[offset + 3] = (byte) (((pxc >> aShift1) * aMul) >> aShift2);
                offset += 4;
            }
        }

        return rawData;
    }

    private unsafe byte[] Decompress3Dc(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];
        var yColours = new byte[8];
        var xColours = new byte[8];

        var offset = 0;
        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y += 4)
            {
                for (var x = 0; x < width; x += 4)
                {
                    var temp2 = temp + 8;

                    //Read Y palette
                    int t1 = yColours[0] = temp[0];
                    int t2 = yColours[1] = temp[1];
                    temp += 2;
                    if (t1 > t2)
                    {
                        for (var i = 2; i < 8; ++i)
                            yColours[i] = (byte) (t1 + (t2 - t1) * (i - 1) / 7);
                    }
                    else
                    {
                        for (var i = 2; i < 6; ++i)
                            yColours[i] = (byte) (t1 + (t2 - t1) * (i - 1) / 5);
                        yColours[6] = 0;
                        yColours[7] = 255;
                    }

                    // Read X palette
                    t1 = xColours[0] = temp2[0];
                    t2 = xColours[1] = temp2[1];
                    temp2 += 2;
                    if (t1 > t2)
                    {
                        for (var i = 2; i < 8; ++i)
                            xColours[i] = (byte) (t1 + (t2 - t1) * (i - 1) / 7);
                    }
                    else
                    {
                        for (var i = 2; i < 6; ++i)
                            xColours[i] = (byte) (t1 + (t2 - t1) * (i - 1) / 5);
                        xColours[6] = 0;
                        xColours[7] = 255;
                    }

                    //decompress pixel data
                    var currentOffset = offset;
                    for (var k = 0; k < 4; k += 2)
                    {
                        // First three bytes
                        var bitmask = ((uint) temp[0] << 0) | ((uint) temp[1] << 8) | ((uint) temp[2] << 16);
                        var bitmask2 = ((uint) temp2[0] << 0) | ((uint) temp2[1] << 8) | ((uint) temp2[2] << 16);
                        for (var j = 0; j < 2; j++)
                            // only put pixels out < height
                            if (y + k + j < height)
                            {
                                for (var i = 0; i < 4; i++)
                                {
                                    // only put pixels out < width
                                    if (x + i < width)
                                    {
                                        int t;
                                        byte tx, ty;

                                        t1 = currentOffset + (x + i) * 3;
                                        rawData[t1 + 1] = ty = yColours[bitmask & 0x07];
                                        rawData[t1 + 0] = tx = xColours[bitmask2 & 0x07];

                                        //calculate b (z) component ((r/255)^2 + (g/255)^2 + (b/255)^2 = 1
                                        t = 127 * 128 - (tx - 127) * (tx - 128) - (ty - 127) * (ty - 128);
                                        if (t > 0)
                                            rawData[t1 + 2] = (byte) (Math.Sqrt(t) + 128);
                                        else
                                            rawData[t1 + 2] = 0x7F;
                                    }

                                    bitmask >>= 3;
                                    bitmask2 >>= 3;
                                }

                                currentOffset += bps;
                            }

                        temp += 3;
                        temp2 += 3;
                    }

                    //skip bytes that were read via Temp2
                    temp += 8;
                }

                offset += bps * 4;
            }
        }

        return rawData;
    }

    private unsafe byte[] DecompressAti1n(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];
        var colors = new byte[8];

        uint offset = 0;
        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y += 4)
            {
                for (var x = 0; x < width; x += 4)
                {
                    //Read palette
                    int t1 = colors[0] = temp[0];
                    int t2 = colors[1] = temp[1];
                    temp += 2;
                    if (t1 > t2)
                    {
                        for (var i = 2; i < 8; ++i)
                            colors[i] = (byte) (t1 + (t2 - t1) * (i - 1) / 7);
                    }
                    else
                    {
                        for (var i = 2; i < 6; ++i)
                            colors[i] = (byte) (t1 + (t2 - t1) * (i - 1) / 5);
                        colors[6] = 0;
                        colors[7] = 255;
                    }

                    //decompress pixel data
                    var currOffset = offset;
                    for (var k = 0; k < 4; k += 2)
                    {
                        // First three bytes
                        var bitmask = ((uint) temp[0] << 0) | ((uint) temp[1] << 8) | ((uint) temp[2] << 16);
                        for (var j = 0; j < 2; j++)
                            // only put pixels out < height
                            if (y + k + j < height)
                            {
                                for (var i = 0; i < 4; i++)
                                {
                                    // only put pixels out < width
                                    if (x + i < width)
                                    {
                                        t1 = (int) (currOffset + (x + i));
                                        rawData[t1] = colors[bitmask & 0x07];
                                    }

                                    bitmask >>= 3;
                                }

                                currOffset += (uint) bps;
                            }

                        temp += 3;
                    }
                }

                offset += (uint) (bps * 4);
            }
        }

        return rawData;
    }

    private unsafe byte[] DecompressLum(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];

        var lShift1 = 0;
        var lMul = 0;
        var lShift2 = 0;
        ComputeMaskParams(header.pixelformat.rbitmask, ref lShift1, ref lMul, ref lShift2);

        var offset = 0;
        var pixnum = width * height * depth;
        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            while (pixnum-- > 0)
            {
                var px = *temp++;
                rawData[offset + 0] = (byte) (((px >> lShift1) * lMul) >> lShift2);
                rawData[offset + 1] = (byte) (((px >> lShift1) * lMul) >> lShift2);
                rawData[offset + 2] = (byte) (((px >> lShift1) * lMul) >> lShift2);
                rawData[offset + 3] = (byte) (((px >> lShift1) * lMul) >> lShift2);
                offset += 4;
            }
        }

        return rawData;
    }

    private unsafe byte[] DecompressRXGB(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];

        var color_0 = new Colour565();
        var color_1 = new Colour565();
        var colors = new Colour8888[4];
        var alphas = new byte[8];

        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y += 4)
            for (var x = 0; x < width; x += 4)
            {
                if (y >= height || x >= width)
                    break;
                alphas[0] = temp[0];
                alphas[1] = temp[1];
                var alphamask = temp + 2;
                temp += 8;

                DxtcReadColors(temp, ref color_0, ref color_1);
                temp += 4;

                var bitmask = ((uint*) temp)[1];
                temp += 4;

                colors[0].red = (byte) (color_0.red << 3);
                colors[0].green = (byte) (color_0.green << 2);
                colors[0].blue = (byte) (color_0.blue << 3);
                colors[0].alpha = 0xFF;

                colors[1].red = (byte) (color_1.red << 3);
                colors[1].green = (byte) (color_1.green << 2);
                colors[1].blue = (byte) (color_1.blue << 3);
                colors[1].alpha = 0xFF;

                // Four-color block: derive the other two colors.    
                // 00 = color_0, 01 = color_1, 10 = color_2, 11 = color_3
                // These 2-bit codes correspond to the 2-bit fields 
                // stored in the 64-bit block.
                colors[2].blue = (byte) ((2 * colors[0].blue + colors[1].blue + 1) / 3);
                colors[2].green = (byte) ((2 * colors[0].green + colors[1].green + 1) / 3);
                colors[2].red = (byte) ((2 * colors[0].red + colors[1].red + 1) / 3);
                colors[2].alpha = 0xFF;

                colors[3].blue = (byte) ((colors[0].blue + 2 * colors[1].blue + 1) / 3);
                colors[3].green = (byte) ((colors[0].green + 2 * colors[1].green + 1) / 3);
                colors[3].red = (byte) ((colors[0].red + 2 * colors[1].red + 1) / 3);
                colors[3].alpha = 0xFF;

                var k = 0;
                for (var j = 0; j < 4; j++)
                for (var i = 0; i < 4; i++, k++)
                {
                    var select = (int) ((bitmask & (0x03 << (k * 2))) >> (k * 2));
                    var col = colors[select];

                    // only put pixels out < width or height
                    if (x + i < width && y + j < height)
                    {
                        var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp);
                        rawData[offset + 0] = col.red;
                        rawData[offset + 1] = col.green;
                        rawData[offset + 2] = col.blue;
                    }
                }

                // 8-alpha or 6-alpha block?    
                if (alphas[0] > alphas[1])
                {
                    // 8-alpha block:  derive the other six alphas.    
                    // Bit code 000 = alpha_0, 001 = alpha_1, others are interpolated.
                    alphas[2] = (byte) ((6 * alphas[0] + 1 * alphas[1] + 3) / 7); // bit code 010
                    alphas[3] = (byte) ((5 * alphas[0] + 2 * alphas[1] + 3) / 7); // bit code 011
                    alphas[4] = (byte) ((4 * alphas[0] + 3 * alphas[1] + 3) / 7); // bit code 100
                    alphas[5] = (byte) ((3 * alphas[0] + 4 * alphas[1] + 3) / 7); // bit code 101
                    alphas[6] = (byte) ((2 * alphas[0] + 5 * alphas[1] + 3) / 7); // bit code 110
                    alphas[7] = (byte) ((1 * alphas[0] + 6 * alphas[1] + 3) / 7); // bit code 111
                }
                else
                {
                    // 6-alpha block.
                    // Bit code 000 = alpha_0, 001 = alpha_1, others are interpolated.
                    alphas[2] = (byte) ((4 * alphas[0] + 1 * alphas[1] + 2) / 5); // Bit code 010
                    alphas[3] = (byte) ((3 * alphas[0] + 2 * alphas[1] + 2) / 5); // Bit code 011
                    alphas[4] = (byte) ((2 * alphas[0] + 3 * alphas[1] + 2) / 5); // Bit code 100
                    alphas[5] = (byte) ((1 * alphas[0] + 4 * alphas[1] + 2) / 5); // Bit code 101
                    alphas[6] = 0x00; // Bit code 110
                    alphas[7] = 0xFF; // Bit code 111
                }

                // Note: Have to separate the next two loops,
                //	it operates on a 6-byte system.
                // First three bytes
                var bits = *(uint*) alphamask;
                for (var j = 0; j < 2; j++)
                for (var i = 0; i < 4; i++)
                {
                    // only put pixels out < width or height
                    if (x + i < width && y + j < height)
                    {
                        var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp + 3);
                        rawData[offset] = alphas[bits & 0x07];
                    }

                    bits >>= 3;
                }

                // Last three bytes
                bits = *(uint*) &alphamask[3];
                for (var j = 2; j < 4; j++)
                for (var i = 0; i < 4; i++)
                {
                    // only put pixels out < width or height
                    if (x + i < width && y + j < height)
                    {
                        var offset = (uint) (z * sizeofplane + (y + j) * bps + (x + i) * bpp + 3);
                        rawData[offset] = alphas[bits & 0x07];
                    }

                    bits >>= 3;
                }
            }
        }

        return rawData;
    }

    private unsafe byte[] DecompressFloat(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];
        var size = 0;
        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            fixed (byte* destPtr = rawData)
            {
                var destData = destPtr;
                switch (pixelFormat)
                {
                    case PixelFormat.R32F: // Red float, green = blue = max
                        size = width * height * depth * 3;
                        for (int i = 0, j = 0; i < size; i += 3, j++)
                        {
                            ((float*) destData)[i] = ((float*) temp)[j];
                            ((float*) destData)[i + 1] = 1.0f;
                            ((float*) destData)[i + 2] = 1.0f;
                        }

                        break;

                    case PixelFormat.A32B32G32R32F: // Direct copy of float RGBA data
                        Array.Copy(data, rawData, data.Length);
                        break;

                    case PixelFormat.G32R32F: // Red float, green float, blue = max
                        size = width * height * depth * 3;
                        for (int i = 0, j = 0; i < size; i += 3, j += 2)
                        {
                            ((float*) destData)[i] = ((float*) temp)[j];
                            ((float*) destData)[i + 1] = ((float*) temp)[j + 1];
                            ((float*) destData)[i + 2] = 1.0f;
                        }

                        break;

                    case PixelFormat.R16F: // Red float, green = blue = max
                        size = width * height * depth * bpp;
                        ConvR16ToFloat32((uint*) destData, (ushort*) temp, (uint) size);
                        break;

                    case PixelFormat.A16B16G16R16F: // Just convert from half to float.
                        size = width * height * depth * bpp;
                        ConvFloat16ToFloat32((uint*) destData, (ushort*) temp, (uint) size);
                        break;

                    case PixelFormat.G16R16F: // Convert from half to float, set blue = max.
                        size = width * height * depth * bpp;
                        ConvG16R16ToFloat32((uint*) destData, (ushort*) temp, (uint) size);
                        break;
                }
            }
        }

        return rawData;
    }

    private unsafe byte[] DecompressARGB(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        if (Check16BitComponents(header))
            return DecompressARGB16(header, data, pixelFormat);

        var sizeOfData = (int) (header.width * header.pixelformat.rgbbitcount / 8 * header.height * header.depth);
        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];

        if (pixelFormat == PixelFormat.LUMINANCE && header.pixelformat.rgbbitcount == 16 &&
            header.pixelformat.rbitmask == 0xFFFF)
        {
            Array.Copy(data, rawData, data.Length);
            return rawData;
        }

        uint readI = 0, tempBpp;
        uint redL = 0, redR = 0;
        uint greenL = 0, greenR = 0;
        uint blueL = 0, blueR = 0;
        uint alphaL = 0, alphaR = 0;

        GetBitsFromMask(header.pixelformat.rbitmask, ref redL, ref redR);
        GetBitsFromMask(header.pixelformat.gbitmask, ref greenL, ref greenR);
        GetBitsFromMask(header.pixelformat.bbitmask, ref blueL, ref blueR);
        GetBitsFromMask(header.pixelformat.alphabitmask, ref alphaL, ref alphaR);
        tempBpp = header.pixelformat.rgbbitcount / 8;

        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            for (var i = 0; i < sizeOfData; i += bpp)
            {
                //@TODO: This is SLOOOW...
                //but the old version crashed in release build under
                //winxp (and xp is right to stop this code - I always
                //wondered that it worked the old way at all)
                if (sizeOfData - i < 4)
                {
                    //less than 4 byte to write?
                    if (tempBpp == 3)
                        //this branch is extra-SLOOOW
                        readI = (uint) (*temp | (*(temp + 1) << 8) | (*(temp + 2) << 16));
                    else if (tempBpp == 1)
                        readI = *temp;
                    else if (tempBpp == 2)
                        readI = (uint) (temp[0] | (temp[1] << 8));
                }
                else
                {
                    readI = (uint) (temp[0] | (temp[1] << 8) | (temp[2] << 16) | (temp[3] << 24));
                }

                temp += tempBpp;

                rawData[i] =
                    (byte) ((((int) readI & (int) header.pixelformat.rbitmask) >> (int) redR) << (int) redL);

                if (bpp >= 3)
                {
                    rawData[i + 1] = (byte) ((((int) readI & (int) header.pixelformat.gbitmask) >> (int) greenR) <<
                                             (int) greenL);
                    rawData[i + 2] =
                        (byte) ((((int) readI & header.pixelformat.bbitmask) >> (int) blueR) << (int) blueL);

                    if (bpp == 4)
                    {
                        rawData[i + 3] =
                            (byte) ((((int) readI & (int) header.pixelformat.alphabitmask) >> (int) alphaR) <<
                                    (int) alphaL);
                        if (alphaL >= 7)
                            rawData[i + 3] = (byte) (rawData[i + 3] != 0 ? 0xFF : 0x00);
                        else if (alphaL >= 4) rawData[i + 3] = (byte) (rawData[i + 3] | (rawData[i + 3] >> 4));
                    }
                }
                else if (bpp == 2)
                {
                    rawData[i + 1] =
                        (byte) ((((int) readI & (int) header.pixelformat.alphabitmask) >> (int) alphaR) <<
                                (int) alphaL);
                    if (alphaL >= 7)
                        rawData[i + 1] = (byte) (rawData[i + 1] != 0 ? 0xFF : 0x00);
                    else if (alphaL >= 4) rawData[i + 1] = (byte) (rawData[i + 1] | (rawData[i + 3] >> 4));
                }
            }
        }

        return rawData;
    }

    private unsafe byte[] DecompressARGB16(DDSStruct header, byte[] data, PixelFormat pixelFormat)
    {
        // allocate bitmap
        var bpp = (int) PixelFormatToBpp(pixelFormat, header.pixelformat.rgbbitcount);
        var bps = (int) (header.width * bpp * PixelFormatToBpc(pixelFormat));
        var sizeofplane = (int) (bps * header.height);
        var width = (int) header.width;
        var height = (int) header.height;
        var depth = (int) header.depth;

        var sizeOfData = (int) (header.width * header.pixelformat.rgbbitcount / 8 * header.height * header.depth);
        var rawData = new byte[depth * sizeofplane + height * bps + width * bpp];

        uint readI = 0, tempBpp = 0;
        uint redL = 0, redR = 0;
        uint greenL = 0, greenR = 0;
        uint blueL = 0, blueR = 0;
        uint alphaL = 0, alphaR = 0;
        uint redPad = 0, greenPad = 0, bluePad = 0, alphaPad = 0;

        GetBitsFromMask(header.pixelformat.rbitmask, ref redL, ref redR);
        GetBitsFromMask(header.pixelformat.gbitmask, ref greenL, ref greenR);
        GetBitsFromMask(header.pixelformat.bbitmask, ref blueL, ref blueR);
        GetBitsFromMask(header.pixelformat.alphabitmask, ref alphaL, ref alphaR);
        redPad = 16 - CountBitsFromMask(header.pixelformat.rbitmask);
        greenPad = 16 - CountBitsFromMask(header.pixelformat.gbitmask);
        bluePad = 16 - CountBitsFromMask(header.pixelformat.bbitmask);
        alphaPad = 16 - CountBitsFromMask(header.pixelformat.alphabitmask);

        redL += redPad;
        greenL += greenPad;
        blueL += bluePad;
        alphaL += alphaPad;

        tempBpp = header.pixelformat.rgbbitcount / 8;
        fixed (byte* bytePtr = data)
        {
            var temp = bytePtr;
            fixed (byte* destPtr = rawData)
            {
                var destData = destPtr;
                for (var i = 0; i < sizeOfData / 2; i += bpp)
                {
                    //@TODO: This is SLOOOW...
                    //but the old version crashed in release build under
                    //winxp (and xp is right to stop this code - I always
                    //wondered that it worked the old way at all)
                    if (sizeOfData - i < 4)
                    {
                        //less than 4 byte to write?
                        if (tempBpp == 3)
                            //this branch is extra-SLOOOW
                            readI = (uint) (*temp | (*(temp + 1) << 8) | (*(temp + 2) << 16));
                        else if (tempBpp == 1)
                            readI = *temp;
                        else if (tempBpp == 2)
                            readI = (uint) (temp[0] | (temp[1] << 8));
                    }
                    else
                    {
                        readI = (uint) (temp[0] | (temp[1] << 8) | (temp[2] << 16) | (temp[3] << 24));
                    }

                    temp += tempBpp;

                    ((ushort*) destData)[i + 2] =
                        (ushort) ((((int) readI & (int) header.pixelformat.rbitmask) >> (int) redR) << (int) redL);

                    if (bpp >= 3)
                    {
                        ((ushort*) destData)[i + 1] =
                            (ushort) ((((int) readI & (int) header.pixelformat.gbitmask) >> (int) greenR) <<
                                      (int) greenL);
                        ((ushort*) destData)[i] =
                            (ushort) ((((int) readI & (int) header.pixelformat.bbitmask) >> (int) blueR) <<
                                      (int) blueL);

                        if (bpp == 4)
                        {
                            ((ushort*) destData)[i + 3] =
                                (ushort) ((((int) readI & (int) header.pixelformat.alphabitmask) >> (int) alphaR) <<
                                          (int) alphaL);
                            if (alphaL >= 7)
                                ((ushort*) destData)[i + 3] =
                                    (ushort) (((ushort*) destData)[i + 3] != 0 ? 0xFF : 0x00);
                            else if (alphaL >= 4)
                                ((ushort*) destData)[i + 3] =
                                    (ushort) (((ushort*) destData)[i + 3] | (((ushort*) destData)[i + 3] >> 4));
                        }
                    }
                    else if (bpp == 2)
                    {
                        ((ushort*) destData)[i + 1] =
                            (ushort) ((((int) readI & (int) header.pixelformat.alphabitmask) >> (int) alphaR) <<
                                      (int) alphaL);
                        if (alphaL >= 7)
                            ((ushort*) destData)[i + 1] = (ushort) (((ushort*) destData)[i + 1] != 0 ? 0xFF : 0x00);
                        else if (alphaL >= 4)
                            ((ushort*) destData)[i + 1] =
                                (ushort) (((ushort*) destData)[i + 1] | (rawData[i + 3] >> 4));
                    }
                }
            }
        }

        return rawData;
    }

    public static implicit operator DDSImage(Bitmap value)
    {
        return new DDSImage(value);
    }

    public static explicit operator Bitmap(DDSImage value)
    {
        return value.BitmapImage;
    }

    private bool AllEqual<T>(params T[] values)
    {
        if (values == null || values.Length == 0)
            return true;
        return values.All(v => v.Equals(values[0]));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Colour8888
    {
        public byte red;
        public byte green;
        public byte blue;
        public byte alpha;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct Colour565
    {
        public ushort blue; //: 5;
        public ushort green; //: 6;
        public ushort red; //: 5;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DDSStruct
    {
        public uint size; // equals size of struct (which is part of the data file!)
        public uint flags;
        public uint height;
        public uint width;
        public uint sizeorpitch;
        public uint depth;
        public uint mipmapcount;

        public uint alphabitdepth;

        //[MarshalAs(UnmanagedType.U4, SizeConst = 11)]
        public uint[] reserved; //[11];

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PixelFormatStruct
        {
            public uint size; // equals size of struct (which is part of the data file!)
            public uint flags;
            public uint fourcc;
            public uint rgbbitcount;
            public uint rbitmask;
            public uint gbitmask;
            public uint bbitmask;
            public uint alphabitmask;
        }

        public PixelFormatStruct pixelformat;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DDSCapsStruct
        {
            public uint caps1;
            public uint caps2;
            public uint caps3;
            public uint caps4;
        }

        public DDSCapsStruct ddscaps;
        public uint texturestage;

        //#ifndef __i386__
        //void to_little_endian()
        //{
        //	size_t size = sizeof(DDSStruct);
        //	assert(size % 4 == 0);
        //	size /= 4;
        //	for (size_t i=0; i<size; i++)
        //	{
        //		((int32_t*) this)[i] = little_endian(((int32_t*) this)[i]);
        //	}
        //}
        //#endif
    }

    /// <summary>
    ///     Various pixel formats/compressors used by the DDS image.
    /// </summary>
    private enum PixelFormat
    {
        /// <summary>
        ///     32-bit image, with 8-bit red, green, blue and alpha.
        /// </summary>
        RGBA,

        /// <summary>
        ///     24-bit image with 8-bit red, green, blue.
        /// </summary>
        RGB,

        /// <summary>
        ///     16-bit DXT-1 compression, 1-bit alpha.
        /// </summary>
        DXT1,

        /// <summary>
        ///     DXT-2 Compression
        /// </summary>
        DXT2,

        /// <summary>
        ///     DXT-3 Compression
        /// </summary>
        DXT3,

        /// <summary>
        ///     DXT-4 Compression
        /// </summary>
        DXT4,

        /// <summary>
        ///     DXT-5 Compression
        /// </summary>
        DXT5,

        /// <summary>
        ///     3DC Compression
        /// </summary>
        THREEDC,

        /// <summary>
        ///     ATI1n Compression
        /// </summary>
        ATI1N,
        LUMINANCE,
        LUMINANCE_ALPHA,
        RXGB,
        A16B16G16R16,
        R16F,
        G16R16F,
        A16B16G16R16F,
        R32F,
        G32R32F,
        A32B32G32R32F,

        /// <summary>
        ///     Unknown pixel format.
        /// </summary>
        UNKNOWN
    }
}

/// <summary>
///     Thrown when an invalid file header has been encountered.
/// </summary>
public class InvalidFileHeaderException : Exception
{
}

/// <summary>
///     Thrown when the data does not contain a DDS image.
/// </summary>
public class NotADDSImageException : Exception
{
}

/// <summary>
///     Thrown when there is an unknown compressor used in the DDS file.
/// </summary>
public class UnknownFileFormatException : Exception
{
}