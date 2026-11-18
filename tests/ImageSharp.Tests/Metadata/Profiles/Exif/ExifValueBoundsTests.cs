// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using Xunit;

namespace SixLabors.ImageSharp.Tests.Metadata.Profiles.Exif
{
    [Trait("Profile", "Exif")]
    public class ExifValueBoundsTests
    {
        private const ushort ImageDescriptionTag = 270;

        private const ushort FreeOffsetsTag = 288;

        private const ushort PageNumberTag = 297;

        private const ushort ReferenceBlackWhiteTag = 532;

        private const ushort ArtistTag = 315;

        private static readonly byte[] Description = Encoding.ASCII.GetBytes("EXIF value bounds\0");

        private static readonly byte[] Artist = Encoding.ASCII.GetBytes("Value bounds artist\0");

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void JpegExifValueWrappingPastEndOfData_IsDropped(bool bigEndian)
        {
            byte[] exif = new ExifPayloadBuilder(bigEndian)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, 0xFFFFFFF0U, _ => 16)
                .AddExternal(ArtistTag, ExifDataType.Ascii, Artist)
                .Build();
            byte[] jpeg = BuildJpeg(exif);

            using (var stream = new MemoryStream(jpeg))
            {
                IImageInfo info = Image.Identify(stream);
                Assert.Equal(1, info.Width);
                Assert.Equal(1, info.Height);
                AssertDropped(info.Metadata.ExifProfile, ImageDescriptionTag);
            }

            using (var stream = new MemoryStream(jpeg))
            {
                using Image image = Image.Load(stream);
                Assert.Equal(1, image.Width);
                Assert.Equal(1, image.Height);
                AssertDropped(image.Metadata.ExifProfile, ImageDescriptionTag);
            }
        }

        [Fact]
        public void JpegExifValidExternalValue_IsRead()
        {
            byte[] exif = new ExifPayloadBuilder(false)
                .AddExternal(ImageDescriptionTag, ExifDataType.Ascii, Description)
                .Build();
            byte[] jpeg = BuildJpeg(exif);

            using (var stream = new MemoryStream(jpeg))
            {
                IImageInfo info = Image.Identify(stream);
                AssertValidDescription(info.Metadata.ExifProfile);
            }

            using (var stream = new MemoryStream(jpeg))
            {
                using Image image = Image.Load(stream);
                AssertValidDescription(image.Metadata.ExifProfile);
            }
        }

        [Fact]
        public void ExternalValueEndingAtEndOfData_IsRead()
        {
            byte[] exif = new ExifPayloadBuilder(false)
                .AddExternal(ImageDescriptionTag, ExifDataType.Ascii, Description)
                .Build();

            Assert.Equal(Description, exif.Skip(exif.Length - Description.Length).ToArray());
            AssertValidDescription(new ExifProfile(exif));
        }

        [Fact]
        public void ExternalValueEndingOneBytePastEndOfData_IsDropped()
        {
            const uint count = 16;
            byte[] exif = new ExifPayloadBuilder(false)
                .AddRaw(ImageDescriptionTag, ExifDataType.Ascii, count, length => (uint)length - count + 1)
                .AddExternal(ArtistTag, ExifDataType.Ascii, Artist)
                .Build();

            AssertDropped(new ExifProfile(exif), ImageDescriptionTag);
        }

        [Theory]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0xFFFFFFF0U, 16U)]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0x80000001U, 0x7FFFFFFFU)]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0x80000000U, 16U)]
        [InlineData(ImageDescriptionTag, ExifDataType.Ascii, 0x7FFFFFFFU, 0x80000001U)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x1FFFFFFFU, 0x80000004U)]
        [InlineData(PageNumberTag, ExifDataType.Short, 0x7FFFFFF8U, 16U)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x3FFFFFFCU, 16U)]
        [InlineData(ReferenceBlackWhiteTag, ExifDataType.Rational, 0x1FFFFFFEU, 16U)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x40000001U, 16U)]
        [InlineData(FreeOffsetsTag, ExifDataType.Long, 0x40000002U, 16U)]
        [InlineData(ReferenceBlackWhiteTag, ExifDataType.Rational, 0x20000000U, 16U)]
        [InlineData(ReferenceBlackWhiteTag, ExifDataType.Rational, 0x20000001U, 16U)]
        public void MalformedExternalValue_IsDropped(ushort tag, ExifDataType dataType, uint count, uint offset)
        {
            byte[] exif = new ExifPayloadBuilder(false)
                .AddRaw(tag, dataType, count, _ => offset)
                .AddExternal(ArtistTag, ExifDataType.Ascii, Artist)
                .Build();

            AssertDropped(new ExifProfile(exif), tag);
        }

        private static void AssertDropped(ExifProfile profile, ushort tag)
        {
            Assert.NotNull(profile);
            Assert.DoesNotContain(profile.Values, value => (ushort)value.Tag == tag);
            Assert.Contains(profile.InvalidTags, invalidTag => (ushort)invalidTag == tag);
            Assert.Equal("JPG", profile.GetValue(ExifTag.Software).Value);
            Assert.Equal("Value bounds artist", profile.GetValue(ExifTag.Artist).Value);
        }

        private static void AssertValidDescription(ExifProfile profile)
        {
            Assert.NotNull(profile);
            Assert.Equal("EXIF value bounds", profile.GetValue(ExifTag.ImageDescription).Value);
            Assert.Equal("JPG", profile.GetValue(ExifTag.Software).Value);
            Assert.Empty(profile.InvalidTags);
        }

        private static byte[] BuildJpeg(byte[] exif)
        {
            using var stream = new MemoryStream();
            stream.Write(new byte[] { 0xFF, 0xD8 });

            int app1Length = 2 + 6 + exif.Length;
            stream.Write(new byte[] { 0xFF, 0xE1, (byte)(app1Length >> 8), (byte)app1Length });
            stream.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
            stream.Write(exif);

            stream.Write(new byte[] { 0xFF, 0xDB, 0x00, 0x43, 0x00 });
            stream.Write(Enumerable.Repeat((byte)1, 64).ToArray());

            stream.Write(new byte[] { 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00 });

            byte[] singleCodeTable = new byte[17];
            singleCodeTable[0] = 1;
            stream.Write(new byte[] { 0xFF, 0xC4, 0x00, 0x14, 0x00 });
            stream.Write(singleCodeTable);
            stream.Write(new byte[] { 0xFF, 0xC4, 0x00, 0x14, 0x10 });
            stream.Write(singleCodeTable);

            stream.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 });
            stream.WriteByte(0x3F);
            stream.Write(new byte[] { 0xFF, 0xD9 });
            return stream.ToArray();
        }

        private sealed class ExifPayloadBuilder
        {
            private const int HeaderSize = 8;

            private const int EntrySize = 12;

            private readonly bool bigEndian;

            private readonly List<Entry> entries = new();

            public ExifPayloadBuilder(bool bigEndian)
            {
                this.bigEndian = bigEndian;
                this.entries.Add(new Entry(305, ExifDataType.Ascii, 4, Encoding.ASCII.GetBytes("JPG\0"), null, null));
            }

            public ExifPayloadBuilder AddExternal(ushort tag, ExifDataType dataType, byte[] value)
            {
                uint count = (uint)value.Length / ExifDataTypes.GetSize(dataType);
                this.entries.Add(new Entry(tag, dataType, count, null, value, null));
                return this;
            }

            public ExifPayloadBuilder AddRaw(ushort tag, ExifDataType dataType, uint count, Func<long, uint> offset)
            {
                this.entries.Add(new Entry(tag, dataType, count, null, null, offset));
                return this;
            }

            public byte[] Build()
            {
                int externalOffset = HeaderSize + 2 + (this.entries.Count * EntrySize) + 4;
                int length = externalOffset + this.entries.Sum(entry => entry.External?.Length ?? 0);

                byte[] data = new byte[length];
                data[0] = data[1] = this.bigEndian ? (byte)'M' : (byte)'I';
                this.WriteUInt16(data, 2, 42);
                this.WriteUInt32(data, 4, HeaderSize);
                this.WriteUInt16(data, HeaderSize, (ushort)this.entries.Count);

                int position = HeaderSize + 2;
                foreach (Entry entry in this.entries)
                {
                    this.WriteUInt16(data, position, entry.Tag);
                    this.WriteUInt16(data, position + 2, (ushort)entry.DataType);
                    this.WriteUInt32(data, position + 4, entry.Count);
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
                        this.WriteUInt32(data, position + 8, entry.Offset(length));
                    }

                    position += EntrySize;
                }

                this.WriteUInt32(data, position, 0);
                return data;
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

            private sealed class Entry
            {
                public Entry(ushort tag, ExifDataType dataType, uint count, byte[] inline, byte[] external, Func<long, uint> offset)
                {
                    this.Tag = tag;
                    this.DataType = dataType;
                    this.Count = count;
                    this.Inline = inline;
                    this.External = external;
                    this.Offset = offset;
                }

                public ushort Tag { get; }

                public ExifDataType DataType { get; }

                public uint Count { get; }

                public byte[] Inline { get; }

                public byte[] External { get; }

                public Func<long, uint> Offset { get; }
            }
        }
    }
}
