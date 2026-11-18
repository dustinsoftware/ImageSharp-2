// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Tests.TestUtilities;
using Xunit;

namespace SixLabors.ImageSharp.Tests.Formats.Tiff
{
    [Trait("Format", "Tiff")]
    public class TiffMetadataBoundsTests
    {
        private const ushort ImageDescriptionTag = 270;

        private const ushort FreeOffsetsTag = 288;

        private const ushort ArtistTag = 315;

        private const ushort PageNumberTag = 297;

        private const ushort ReferenceBlackWhiteTag = 532;

        private const ushort PixelScaleTag = 33550;

        private const ushort UnknownTag = 50000;

        private const uint InFileOffset = 16;

        private const long LargeStreamLength = 1L << 33;

        private static readonly byte[] Description = Encoding.ASCII.GetBytes("TIFF metadata bounds\0");

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ValidMetadata_IsRead(bool bigEndian)
        {
            var builder = new ClassicTiffBuilder(bigEndian);
            byte[] artist = Encoding.ASCII.GetBytes("Metadata bounds artist\0");
            byte[] data = builder
                .AddInline(PageNumberTag, ExifDataType.Short, 2, builder.EncodeShorts(1, 2))
                .AddExternal(ImageDescriptionTag, ExifDataType.Ascii, Description)
                .AddExternal(ArtistTag, ExifDataType.Ascii, artist)
                .Build();

            var allocator = new BoundedRecordingAllocator();
            IImageInfo info = Identify(data, allocator);
            Assert.Equal(72, info.Metadata.HorizontalResolution);
            Assert.Equal(72, info.Metadata.VerticalResolution);

            using Image image = Load(data, allocator);
            ExifProfile profile = image.Frames.RootFrame.Metadata.ExifProfile;
            Assert.Equal("TIF", profile.GetValue(ExifTag.Software).Value);
            Assert.Equal(new ushort[] { 1, 2 }, profile.GetValue(ExifTag.PageNumber).Value);
            Assert.Equal("TIFF metadata bounds", profile.GetValue(ExifTag.ImageDescription).Value);
            Assert.Equal("Metadata bounds artist", profile.GetValue(ExifTag.Artist).Value);
            Assert.Equal(new Rational(72, 1), profile.GetValue(ExifTag.XResolution).Value);
            Assert.Empty(profile.InvalidTags);
            Assert.All(allocator.RequestedBytes, bytes => Assert.True(bytes < data.Length));
        }

        [Fact]
        public void ValidExternalValueEndingAtEndOfStream_IsRead()
        {
            byte[] data = new ClassicTiffBuilder(false)
                .AddExternal(ImageDescriptionTag, ExifDataType.Ascii, Description)
                .Build();

            Assert.Equal(Description, data.Skip(data.Length - Description.Length).ToArray());

            var allocator = new BoundedRecordingAllocator();
            Identify(data, allocator);

            using Image image = Load(data, allocator);
            ExifProfile profile = image.Frames.RootFrame.Metadata.ExifProfile;
            Assert.Equal("TIFF metadata bounds", profile.GetValue(ExifTag.ImageDescription).Value);
            Assert.Empty(profile.InvalidTags);
            Assert.All(allocator.RequestedBytes, bytes => Assert.True(bytes < data.Length));
        }

        [Fact]
        public void ZeroComponentCount_FallsBackToInlineValue()
        {
            byte[] data = new ClassicTiffBuilder(false)
                .AddInline(UnknownTag, ExifDataType.Undefined, 0, new byte[] { 1, 2, 3, 4 })
                .Build();

            var allocator = new BoundedRecordingAllocator();
            Identify(data, allocator);

            using Image image = Load(data, allocator);
            ExifProfile profile = image.Frames.RootFrame.Metadata.ExifProfile;
            IExifValue value = Assert.Single(profile.Values, value => (ushort)value.Tag == UnknownTag);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, (byte[])value.GetValue());
            Assert.Empty(profile.InvalidTags);
        }

        [Fact]
        public void ValueEndingOneBytePastEndOfStream_IsDropped()
        {
            const uint count = 16;
            byte[] data = new ClassicTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, count, length => (uint)length - count + 1)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, ImageDescriptionTag);
        }

        [Fact]
        public void ValueOneByteLargerThanStream_IsDropped()
        {
            byte[] data = new ClassicTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, length => (uint)length + 1, _ => InFileOffset)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, ImageDescriptionTag);
        }

        [Theory]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0xFFFFFFF0U, 0x20U, false)]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0xFFFFFFF0U, 0x20U, true)]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0x80000001U, 0x7FFFFFFFU, false)]
        [InlineData(PageNumberTag, ExifDataType.Short, 0x7FFFFFF8U, 0x20U, false)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x3FFFFFFCU, 0x20U, false)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x20000001U, 0x7FFFFFFCU, false)]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0x7FFFFFFFU, 0x80000001U, false)]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0x7FFFFFFFU, 0x80000001U, true)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x1FFFFFFFU, 0x80000004U, false)]
        [InlineData(ReferenceBlackWhiteTag, ExifDataType.Rational, 0x1FFFFFFEU, 0x20U, false)]
        [InlineData(PixelScaleTag, ExifDataType.DoubleFloat, 0x1FFFFFFEU, 0x20U, false)]
        public void OffsetPlusSizeWraps_IsDropped(ushort tag, ExifDataType dataType, uint count, uint offset, bool bigEndian)
        {
            byte[] data = new ClassicTiffBuilder(bigEndian)
                .AddRaw(tag, dataType, count, _ => offset)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, tag);
        }

        [Theory]
        [InlineData(0x80000000U, InFileOffset)]
        [InlineData(uint.MaxValue, 0U)]
        [InlineData(5U, uint.MaxValue)]
        [InlineData(5U, 0x80000000U)]
        public void ValueOutsideStream_IsDropped(uint count, uint offset)
        {
            byte[] data = new ClassicTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, count, _ => offset)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, ImageDescriptionTag);
        }

        [Theory]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0x80000000U)]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, uint.MaxValue)]
        [InlineData(PageNumberTag, ExifDataType.Short, 0x40000000U)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x3FFFFFFFU)]
        [InlineData(ReferenceBlackWhiteTag, ExifDataType.Rational, 0x10000000U)]
        [InlineData(PixelScaleTag, ExifDataType.DoubleFloat, 0x1FFFFFFFU)]
        public void ValueLargerThanIntMaxValueInsideLargeStream_IsDropped(ushort tag, ExifDataType dataType, uint count)
        {
            byte[] data = new ClassicTiffBuilder(false)
                .AddRaw(tag, dataType, count, _ => InFileOffset)
                .Build();

            var allocator = new BoundedRecordingAllocator();
            using Image image = IdentifyAndLoadLargeStream(data, allocator);
            AssertDropped(image.Frames.RootFrame.Metadata.ExifProfile, tag);
            Assert.All(allocator.RequestedBytes, bytes => Assert.True(bytes < data.Length));
        }

        [Fact]
        public void ValueBeyondIntMaxValueOffsetInsideLargeStream_IsDropped()
        {
            byte[] data = new ClassicTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, 8, _ => 0x80000000U)
                .Build();

            var allocator = new BoundedRecordingAllocator();
            using Image image = IdentifyAndLoadLargeStream(data, allocator);
            AssertDropped(image.Frames.RootFrame.Metadata.ExifProfile, ImageDescriptionTag);
            Assert.All(allocator.RequestedBytes, bytes => Assert.True(bytes < data.Length));
        }

        [Theory]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x40000001U)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x40000002U)]
        [InlineData(PageNumberTag, ExifDataType.Short, 0x80000002U)]
        [InlineData(PageNumberTag, ExifDataType.Short, 0x80000004U)]
        [InlineData(ReferenceBlackWhiteTag, ExifDataType.Rational, 0x20000000U)]
        [InlineData(ReferenceBlackWhiteTag, ExifDataType.Rational, 0x20000001U)]
        [InlineData(PixelScaleTag, ExifDataType.DoubleFloat, 0x20000000U)]
        [InlineData(PixelScaleTag, ExifDataType.DoubleFloat, 0x20000002U)]
        public void ComponentCountOverflow_IsDropped(ushort tag, ExifDataType dataType, uint count)
        {
            byte[] data = new ClassicTiffBuilder(false)
                .AddRaw(tag, dataType, count, _ => InFileOffset)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, tag);
        }

        private static void AssertDroppedWithoutLargeAllocation(byte[] data, ushort tag)
        {
            var allocator = new BoundedRecordingAllocator();
            Identify(data, allocator);

            using Image image = Load(data, allocator);
            AssertDropped(image.Frames.RootFrame.Metadata.ExifProfile, tag);
            Assert.All(allocator.RequestedBytes, bytes => Assert.True(bytes < data.Length));
        }

        private static void AssertDropped(ExifProfile profile, ushort tag)
        {
            Assert.DoesNotContain(profile.Values, value => (ushort)value.Tag == tag);
            Assert.Contains(profile.InvalidTags, invalidTag => (ushort)invalidTag == tag);
            Assert.Equal("TIF", profile.GetValue(ExifTag.Software).Value);
            Assert.Equal(new Rational(72, 1), profile.GetValue(ExifTag.XResolution).Value);
        }

        private static IImageInfo Identify(byte[] data, BoundedRecordingAllocator allocator)
        {
            using var stream = new MemoryStream(data);
            IImageInfo info = Image.Identify(CreateConfiguration(allocator), stream);
            Assert.Equal(1, info.Width);
            Assert.Equal(1, info.Height);
            return info;
        }

        private static Image Load(byte[] data, BoundedRecordingAllocator allocator)
        {
            using var stream = new MemoryStream(data);
            Image image = Image.Load(CreateConfiguration(allocator), stream);
            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);
            return image;
        }

        private static Image IdentifyAndLoadLargeStream(byte[] data, BoundedRecordingAllocator allocator)
        {
            Configuration configuration = CreateConfiguration(allocator);

            using var identifyStream = new LargeLengthStream(data, LargeStreamLength);
            IImageInfo info = Image.Identify(configuration, identifyStream);
            Assert.Equal(1, info.Width);
            Assert.Equal(1, info.Height);

            using var loadStream = new LargeLengthStream(data, LargeStreamLength);
            Image image = Image.Load(configuration, loadStream);
            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);
            return image;
        }

        private static Configuration CreateConfiguration(MemoryAllocator allocator)
        {
            Configuration configuration = Configuration.Default.Clone();
            configuration.MemoryAllocator = allocator;
            return configuration;
        }

        private sealed class ClassicTiffBuilder
        {
            private const int HeaderSize = 8;

            private const int EntrySize = 12;

            private readonly bool bigEndian;

            private readonly List<Entry> entries = new();

            public ClassicTiffBuilder(bool bigEndian)
            {
                this.bigEndian = bigEndian;
                this.AddShort(256, 1);
                this.AddShort(257, 1);
                this.AddShort(258, 8);
                this.AddShort(259, 1);
                this.AddShort(262, 1);
                this.entries.Add(new Entry(273, ExifDataType.Long, _ => 1, null, null, layout => layout.PixelOffset));
                this.AddShort(277, 1);
                this.AddShort(278, 1);
                this.AddInline(279, ExifDataType.Long, 1, this.EncodeUInt32(1));
                this.AddExternal(282, ExifDataType.Rational, this.EncodeRational(72, 1));
                this.AddExternal(283, ExifDataType.Rational, this.EncodeRational(72, 1));
                this.AddShort(296, 2);
                this.AddInline(305, ExifDataType.Ascii, 4, Encoding.ASCII.GetBytes("TIF\0"));
            }

            public ClassicTiffBuilder AddInline(ushort tag, ExifDataType dataType, uint count, byte[] value)
            {
                this.entries.RemoveAll(entry => entry.Tag == tag);
                this.entries.Add(new Entry(tag, dataType, _ => count, value, null, null));
                return this;
            }

            public ClassicTiffBuilder AddExternal(ushort tag, ExifDataType dataType, byte[] value)
            {
                uint count = (uint)value.Length / ExifDataTypes.GetSize(dataType);
                this.entries.Add(new Entry(tag, dataType, _ => count, null, value, null));
                return this;
            }

            public ClassicTiffBuilder AddRaw(ushort tag, ExifDataType dataType, uint count, Func<long, uint> offset)
                => this.AddRaw(tag, dataType, _ => count, offset);

            public ClassicTiffBuilder AddRaw(ushort tag, ExifDataType dataType, Func<long, uint> count, Func<long, uint> offset)
            {
                this.entries.Add(new Entry(tag, dataType, count, null, null, layout => offset(layout.Length)));
                return this;
            }

            public byte[] EncodeShorts(ushort first, ushort second)
            {
                byte[] encoded = new byte[4];
                this.WriteUInt16(encoded, 0, first);
                this.WriteUInt16(encoded, 2, second);
                return encoded;
            }

            public byte[] Build()
            {
                int ifdSize = 2 + (this.entries.Count * EntrySize) + 4;
                int pixelOffset = HeaderSize + ifdSize;
                int length = pixelOffset + 1 + this.entries.Sum(entry => entry.External?.Length ?? 0);
                var layout = new Layout(length, (uint)pixelOffset);

                byte[] data = new byte[length];
                data[0] = data[1] = this.bigEndian ? (byte)'M' : (byte)'I';
                this.WriteUInt16(data, 2, 42);
                this.WriteUInt32(data, 4, HeaderSize);
                this.WriteUInt16(data, HeaderSize, (ushort)this.entries.Count);
                data[pixelOffset] = 0x80;

                int externalOffset = pixelOffset + 1;
                int position = HeaderSize + 2;
                foreach (Entry entry in this.entries)
                {
                    this.WriteUInt16(data, position, entry.Tag);
                    this.WriteUInt16(data, position + 2, (ushort)entry.DataType);
                    this.WriteUInt32(data, position + 4, entry.Count(length));
                    if (entry.Inline != null)
                    {
                        Buffer.BlockCopy(entry.Inline, 0, data, position + 8, entry.Inline.Length);
                    }
                    else if (entry.External != null)
                    {
                        this.WriteUInt32(data, position + 8, (uint)externalOffset);
                        Buffer.BlockCopy(entry.External, 0, data, externalOffset, entry.External.Length);
                        externalOffset += entry.External.Length;
                    }
                    else
                    {
                        this.WriteUInt32(data, position + 8, entry.ValueField(layout));
                    }

                    position += EntrySize;
                }

                this.WriteUInt32(data, position, 0);
                return data;
            }

            private void AddShort(ushort tag, ushort value)
            {
                byte[] encoded = new byte[2];
                this.WriteUInt16(encoded, 0, value);
                this.AddInline(tag, ExifDataType.Short, 1, encoded);
            }

            private byte[] EncodeUInt32(uint value)
            {
                byte[] encoded = new byte[4];
                this.WriteUInt32(encoded, 0, value);
                return encoded;
            }

            private byte[] EncodeRational(uint numerator, uint denominator)
            {
                byte[] encoded = new byte[8];
                this.WriteUInt32(encoded, 0, numerator);
                this.WriteUInt32(encoded, 4, denominator);
                return encoded;
            }

            private void WriteUInt16(byte[] data, int offset, ushort value)
            {
                Span<byte> span = data.AsSpan(offset, 2);
                if (this.bigEndian)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(span, value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(span, value);
                }
            }

            private void WriteUInt32(byte[] data, int offset, uint value)
            {
                Span<byte> span = data.AsSpan(offset, 4);
                if (this.bigEndian)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(span, value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(span, value);
                }
            }

            private sealed class Layout
            {
                public Layout(long length, uint pixelOffset)
                {
                    this.Length = length;
                    this.PixelOffset = pixelOffset;
                }

                public long Length { get; }

                public uint PixelOffset { get; }
            }

            private sealed class Entry
            {
                public Entry(ushort tag, ExifDataType dataType, Func<long, uint> count, byte[] inline, byte[] external, Func<Layout, uint> valueField)
                {
                    this.Tag = tag;
                    this.DataType = dataType;
                    this.Count = count;
                    this.Inline = inline;
                    this.External = external;
                    this.ValueField = valueField;
                }

                public ushort Tag { get; }

                public ExifDataType DataType { get; }

                public Func<long, uint> Count { get; }

                public byte[] Inline { get; }

                public byte[] External { get; }

                public Func<Layout, uint> ValueField { get; }
            }
        }
    }
}
