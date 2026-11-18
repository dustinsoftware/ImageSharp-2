// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using Xunit;

namespace SixLabors.ImageSharp.Tests.Formats.Tiff
{
    [Trait("Format", "Tiff")]
    public class BigTiffMetadataBoundsTests
    {
        private const ushort ImageDescriptionTag = 270;

        private const ulong InFileOffset = 16;

        private static readonly byte[] Description = Encoding.ASCII.GetBytes("BigTIFF metadata bounds\0");

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ValidMetadata_IsRead(bool bigEndian)
        {
            byte[] artist = Encoding.ASCII.GetBytes("Metadata bounds artist\0");
            byte[] data = new BigTiffBuilder(bigEndian)
                .AddInline(305, ExifDataType.Ascii, 4, Encoding.ASCII.GetBytes("abc\0"))
                .AddExternal(ImageDescriptionTag, ExifDataType.Ascii, Description)
                .AddExternal(315, ExifDataType.Ascii, artist)
                .Build();

            var allocator = new BoundedRecordingAllocator();
            IImageInfo info = Identify(data, allocator);
            Assert.Equal(72, info.Metadata.HorizontalResolution);

            using Image image = Load(data, allocator);
            ExifProfile profile = image.Frames.RootFrame.Metadata.ExifProfile;
            Assert.Equal("abc", profile.GetValue(ExifTag.Software).Value);
            Assert.Equal("BigTIFF metadata bounds", profile.GetValue(ExifTag.ImageDescription).Value);
            Assert.Equal("Metadata bounds artist", profile.GetValue(ExifTag.Artist).Value);
            Assert.Empty(profile.InvalidTags);
            Assert.All(allocator.RequestedBytes, bytes => Assert.True(bytes < data.Length));
        }

        [Fact]
        public void ValidExternalValueEndingAtEndOfStream_IsRead()
        {
            byte[] data = new BigTiffBuilder(false)
                .AddExternal(ImageDescriptionTag, ExifDataType.Ascii, Description)
                .Build();

            Assert.Equal(Description, data.Skip(data.Length - Description.Length).ToArray());

            var allocator = new BoundedRecordingAllocator();
            Identify(data, allocator);

            using Image image = Load(data, allocator);
            ExifProfile profile = image.Frames.RootFrame.Metadata.ExifProfile;
            Assert.Equal("BigTIFF metadata bounds", profile.GetValue(ExifTag.ImageDescription).Value);
            Assert.Empty(profile.InvalidTags);
        }

        [Fact]
        public void ValueEndingOneBytePastEndOfStream_IsDropped()
        {
            const ulong count = 16;
            byte[] data = new BigTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, count, length => (ulong)length - count + 1)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, ImageDescriptionTag);
        }

        [Fact]
        public void ValueOneByteLargerThanStream_IsDroppedWithoutAllocating()
        {
            byte[] data = new BigTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, length => (ulong)length + 1, _ => InFileOffset)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, ImageDescriptionTag);
        }

        [Theory]
        [InlineData(256UL * 1024 * 1024, false)]
        [InlineData(1024UL * 1024 * 1024, false)]
        [InlineData(1024UL * 1024 * 1024, true)]
        [InlineData((ulong)int.MaxValue, false)]
        [InlineData((ulong)int.MaxValue + 1, false)]
        [InlineData((ulong)int.MaxValue + 1, true)]
        [InlineData((1UL << 32) + 16, false)]
        [InlineData(ulong.MaxValue, false)]
        public void ValueLargerThanStream_IsDroppedWithoutAllocating(ulong count, bool bigEndian)
        {
            byte[] data = new BigTiffBuilder(bigEndian)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, count, _ => InFileOffset)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, ImageDescriptionTag);
        }

        [Theory]
        [InlineData(1UL << 31)]
        [InlineData((1UL << 32) + 16)]
        [InlineData((1UL << 32) + (1UL << 31))]
        public void ValueLargerThanIntMaxValueInsideLargeStream_IsDropped(ulong count)
        {
            const long streamLength = 1L << 33;
            byte[] data = new BigTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, count, _ => InFileOffset)
                .Build();

            var allocator = new BoundedRecordingAllocator();
            Configuration configuration = CreateConfiguration(allocator);

            using var identifyStream = new LargeLengthStream(data, streamLength);
            IImageInfo info = Image.Identify(configuration, identifyStream);
            Assert.Equal(1, info.Width);
            Assert.Equal(1, info.Height);

            using var loadStream = new LargeLengthStream(data, streamLength);
            using Image image = Image.Load(configuration, loadStream);
            AssertDropped(image.Frames.RootFrame.Metadata.ExifProfile, ImageDescriptionTag);
            Assert.All(allocator.RequestedBytes, bytes => Assert.True(bytes < data.Length));
        }

        [Theory]
        [InlineData(ulong.MaxValue, 9UL)]
        [InlineData(ulong.MaxValue - 4, 16UL)]
        [InlineData(long.MaxValue, 16UL)]
        public void OffsetOutsideStream_IsDropped(ulong offset, ulong count)
        {
            byte[] data = new BigTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, count, _ => offset)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, ImageDescriptionTag);
        }

        [Fact]
        public void OffsetAtEndOfStream_IsDropped()
        {
            byte[] data = new BigTiffBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, 9, length => (ulong)length)
                .Build();

            AssertDroppedWithoutLargeAllocation(data, ImageDescriptionTag);
        }

        [Theory]
        [InlineData(532, ExifDataType.Rational, (1UL << 61) + 1)]
        [InlineData(532, ExifDataType.Rational, (1UL << 61) + 2)]
        [InlineData(532, ExifDataType.Rational, 1UL << 61)]
        [InlineData(297, ExifDataType.Short, (1UL << 63) + 1)]
        [InlineData(288, ExifDataType.Long, (1UL << 62) + 1)]
        [InlineData(288, ExifDataType.Long, (1UL << 62) + 4)]
        public void ComponentCountOverflow_IsDropped(ushort tag, ExifDataType dataType, ulong count)
        {
            byte[] data = new BigTiffBuilder(false)
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
            Assert.Equal("BigTIFF", profile.GetValue(ExifTag.Software).Value);
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

        private static Configuration CreateConfiguration(MemoryAllocator allocator)
        {
            Configuration configuration = Configuration.Default.Clone();
            configuration.MemoryAllocator = allocator;
            return configuration;
        }

        private sealed class BoundedRecordingAllocator : MemoryAllocator
        {
            private const int MaxRequestBytes = 1024 * 1024;

            private readonly MemoryAllocator inner = new SimpleGcMemoryAllocator();

            public List<long> RequestedBytes { get; } = new();

            public override IMemoryOwner<T> Allocate<T>(int length, AllocationOptions options = AllocationOptions.None)
            {
                long bytes = (long)length * Unsafe.SizeOf<T>();
                this.RequestedBytes.Add(bytes);
                if (bytes > MaxRequestBytes)
                {
                    throw new InvalidOperationException($"Allocation request of {bytes} bytes exceeds the test limit of {MaxRequestBytes} bytes.");
                }

                return this.inner.Allocate<T>(length, options);
            }

            protected internal override int GetBufferCapacityInBytes() => MaxRequestBytes;
        }

        private sealed class LargeLengthStream : Stream
        {
            private readonly byte[] prefix;

            private readonly long length;

            private long position;

            public LargeLengthStream(byte[] prefix, long length)
            {
                this.prefix = prefix;
                this.length = length;
            }

            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => this.length;

            public override long Position
            {
                get => this.position;
                set => this.position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = (int)Math.Max(0, Math.Min(count, this.length - this.position));
                for (int i = 0; i < read; i++)
                {
                    long source = this.position + i;
                    buffer[offset + i] = source < this.prefix.Length ? this.prefix[source] : (byte)0;
                }

                this.position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                this.position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => this.position + offset,
                    _ => this.length + offset
                };

                return this.position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class BigTiffBuilder
        {
            private const int HeaderSize = 16;

            private const int EntrySize = 20;

            private readonly bool bigEndian;

            private readonly List<Entry> entries = new();

            public BigTiffBuilder(bool bigEndian)
            {
                this.bigEndian = bigEndian;
                this.AddShort(256, 1);
                this.AddShort(257, 1);
                this.AddShort(258, 8);
                this.AddShort(259, 1);
                this.AddShort(262, 1);
                this.entries.Add(new Entry(273, ExifDataType.Long8, _ => 1, null, null, layout => layout.PixelOffset));
                this.AddShort(277, 1);
                this.AddShort(278, 1);
                this.AddInline(279, ExifDataType.Long8, 1, this.Encode(1UL));
                this.AddInline(282, ExifDataType.Rational, 1, this.EncodeRational(72, 1));
                this.AddInline(283, ExifDataType.Rational, 1, this.EncodeRational(72, 1));
                this.AddShort(296, 2);
                this.AddInline(305, ExifDataType.Ascii, 8, Encoding.ASCII.GetBytes("BigTIFF\0"));
            }

            public BigTiffBuilder AddInline(ushort tag, ExifDataType dataType, ulong count, byte[] value)
            {
                this.entries.RemoveAll(entry => entry.Tag == tag);
                this.entries.Add(new Entry(tag, dataType, _ => count, value, null, null));
                return this;
            }

            public BigTiffBuilder AddExternal(ushort tag, ExifDataType dataType, byte[] value)
            {
                this.entries.Add(new Entry(tag, dataType, _ => (ulong)value.Length, null, value, null));
                return this;
            }

            public BigTiffBuilder AddRaw(ushort tag, ExifDataType dataType, ulong count, Func<long, ulong> offset)
                => this.AddRaw(tag, dataType, _ => count, offset);

            public BigTiffBuilder AddRaw(ushort tag, ExifDataType dataType, Func<long, ulong> count, Func<long, ulong> offset)
            {
                this.entries.Add(new Entry(tag, dataType, count, null, null, layout => offset(layout.Length)));
                return this;
            }

            public byte[] Build()
            {
                int ifdSize = 8 + (this.entries.Count * EntrySize) + 8;
                int pixelOffset = HeaderSize + ifdSize;
                int length = pixelOffset + 1 + this.entries.Sum(entry => entry.External?.Length ?? 0);
                var layout = new Layout(length, (ulong)pixelOffset);

                byte[] data = new byte[length];
                data[0] = data[1] = this.bigEndian ? (byte)'M' : (byte)'I';
                this.WriteUInt16(data, 2, 43);
                this.WriteUInt16(data, 4, 8);
                this.WriteUInt64(data, 8, HeaderSize);
                this.WriteUInt64(data, HeaderSize, (ulong)this.entries.Count);
                data[pixelOffset] = 0x80;

                int externalOffset = pixelOffset + 1;
                int position = HeaderSize + 8;
                foreach (Entry entry in this.entries)
                {
                    this.WriteUInt16(data, position, entry.Tag);
                    this.WriteUInt16(data, position + 2, (ushort)entry.DataType);
                    this.WriteUInt64(data, position + 4, entry.Count(length));
                    if (entry.Inline != null)
                    {
                        Buffer.BlockCopy(entry.Inline, 0, data, position + 12, entry.Inline.Length);
                    }
                    else if (entry.External != null)
                    {
                        this.WriteUInt64(data, position + 12, (ulong)externalOffset);
                        Buffer.BlockCopy(entry.External, 0, data, externalOffset, entry.External.Length);
                        externalOffset += entry.External.Length;
                    }
                    else
                    {
                        this.WriteUInt64(data, position + 12, entry.ValueField(layout));
                    }

                    position += EntrySize;
                }

                this.WriteUInt64(data, position, 0);
                return data;
            }

            private void AddShort(ushort tag, ushort value)
            {
                byte[] encoded = new byte[2];
                if (this.bigEndian)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(encoded, value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(encoded, value);
                }

                this.AddInline(tag, ExifDataType.Short, 1, encoded);
            }

            private byte[] Encode(ulong value)
            {
                byte[] encoded = new byte[8];
                this.WriteUInt64(encoded, 0, value);
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

            private void WriteUInt64(byte[] data, int offset, ulong value)
            {
                Span<byte> span = data.AsSpan(offset, 8);
                if (this.bigEndian)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(span, value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(span, value);
                }
            }

            private sealed class Layout
            {
                public Layout(long length, ulong pixelOffset)
                {
                    this.Length = length;
                    this.PixelOffset = pixelOffset;
                }

                public long Length { get; }

                public ulong PixelOffset { get; }
            }

            private sealed class Entry
            {
                public Entry(ushort tag, ExifDataType dataType, Func<long, ulong> count, byte[] inline, byte[] external, Func<Layout, ulong> valueField)
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

                public Func<long, ulong> Count { get; }

                public byte[] Inline { get; }

                public byte[] External { get; }

                public Func<Layout, ulong> ValueField { get; }
            }
        }
    }
}
