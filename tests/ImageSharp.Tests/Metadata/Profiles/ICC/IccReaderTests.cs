// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using Xunit;

namespace SixLabors.ImageSharp.Tests.Metadata.Profiles.Icc
{
    [Trait("Profile", "Icc")]
    public class IccReaderTests
    {
        [Fact]
        public void ReadProfile_NoEntries()
        {
            IccReader reader = this.CreateReader();

            IccProfile output = reader.Read(IccTestDataProfiles.Header_Random_Array);

            Assert.Equal(0, output.Entries.Length);
            Assert.NotNull(output.Header);

            IccProfileHeader header = output.Header;
            IccProfileHeader expected = IccTestDataProfiles.Header_Random_Read;
            Assert.Equal(header.Class, expected.Class);
            Assert.Equal(header.CmmType, expected.CmmType);
            Assert.Equal(header.CreationDate, expected.CreationDate);
            Assert.Equal(header.CreatorSignature, expected.CreatorSignature);
            Assert.Equal(header.DataColorSpace, expected.DataColorSpace);
            Assert.Equal(header.DeviceAttributes, expected.DeviceAttributes);
            Assert.Equal(header.DeviceManufacturer, expected.DeviceManufacturer);
            Assert.Equal(header.DeviceModel, expected.DeviceModel);
            Assert.Equal(header.FileSignature, expected.FileSignature);
            Assert.Equal(header.Flags, expected.Flags);
            Assert.Equal(header.Id, expected.Id);
            Assert.Equal(header.PcsIlluminant, expected.PcsIlluminant);
            Assert.Equal(header.PrimaryPlatformSignature, expected.PrimaryPlatformSignature);
            Assert.Equal(header.ProfileConnectionSpace, expected.ProfileConnectionSpace);
            Assert.Equal(header.RenderingIntent, expected.RenderingIntent);
            Assert.Equal(header.Size, expected.Size);
            Assert.Equal(header.Version, expected.Version);
        }

        [Fact]
        public void ReadProfile_DuplicateEntry()
        {
            IccReader reader = this.CreateReader();

            IccProfile output = reader.Read(IccTestDataProfiles.Profile_Random_Array);

            Assert.Equal(2, output.Entries.Length);
            Assert.True(ReferenceEquals(output.Entries[0], output.Entries[1]));
        }

        [Fact]
        public void ReadProfile_WithUndersizedArrayTags_IgnoresTags()
        {
            const int tagCount = 100;
            const int dataOffset = 132 + (tagCount * 12);
            byte[] data = new byte[dataOffset + 16];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), tagCount);

            for (int i = 0; i < tagCount; i++)
            {
                Span<byte> entry = data.AsSpan(132 + (i * 12), 12);
                BinaryPrimitives.WriteUInt32BigEndian(entry, 0x73663332);
                BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(4), dataOffset);
                BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(8), 0);
            }

            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(dataOffset), 0x73663332);
#if NETCOREAPP
            const long MaxAllocatedBytes = 1024 * 1024;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
#endif
            var profile = new IccProfile(data);
            IccTagDataEntry[] entries = profile.Entries;
#if NETCOREAPP
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.True(allocatedBytes < MaxAllocatedBytes, $"Reading the profile allocated {allocatedBytes} bytes.");
#endif

            Assert.Empty(entries);
        }

        [Fact]
        public void ReadProfile_WithUnequalClutGridPoints_ReadsMultiProcessElementsTag()
        {
            const int HeaderLength = 128;
            const int TagTableOffset = HeaderLength + 4;
            const int TagDataOffset = TagTableOffset + 12;
            const int MultiProcessElementsHeaderLength = 24;
            const int ClutElementHeaderLength = 8;
            const int GridPointsLength = 16;
            const int InputChannelCount = 3;
            const int OutputChannelCount = 1;
            const int NodeCount = 2 * 3 * 4;
            const int ClutElementLength = ClutElementHeaderLength + GridPointsLength + (NodeCount * OutputChannelCount * sizeof(float));
            const int TagDataLength = MultiProcessElementsHeaderLength + ClutElementLength;
            const uint DToB0TagSignature = 0x44324230;
            const uint MultiProcessElementsTypeSignature = 0x6D706574;
            const uint ClutElementSignature = 0x636C7574;
            byte[] gridPointCount = { 2, 3, 4 };

            byte[] data = new byte[TagDataOffset + TagDataLength];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), (uint)data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(HeaderLength), 1);

            Span<byte> tagEntry = data.AsSpan(TagTableOffset, 12);
            BinaryPrimitives.WriteUInt32BigEndian(tagEntry, DToB0TagSignature);
            BinaryPrimitives.WriteUInt32BigEndian(tagEntry.Slice(4), TagDataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(tagEntry.Slice(8), TagDataLength);

            Span<byte> tagData = data.AsSpan(TagDataOffset, TagDataLength);
            BinaryPrimitives.WriteUInt32BigEndian(tagData, MultiProcessElementsTypeSignature);
            BinaryPrimitives.WriteUInt16BigEndian(tagData.Slice(8), InputChannelCount);
            BinaryPrimitives.WriteUInt16BigEndian(tagData.Slice(10), OutputChannelCount);
            BinaryPrimitives.WriteUInt32BigEndian(tagData.Slice(12), 1);
            BinaryPrimitives.WriteUInt32BigEndian(tagData.Slice(16), MultiProcessElementsHeaderLength);
            BinaryPrimitives.WriteUInt32BigEndian(tagData.Slice(20), ClutElementLength);

            Span<byte> element = tagData.Slice(MultiProcessElementsHeaderLength);
            BinaryPrimitives.WriteUInt32BigEndian(element, ClutElementSignature);
            BinaryPrimitives.WriteUInt16BigEndian(element.Slice(4), InputChannelCount);
            BinaryPrimitives.WriteUInt16BigEndian(element.Slice(6), OutputChannelCount);
            gridPointCount.CopyTo(element.Slice(ClutElementHeaderLength));

            Span<byte> values = element.Slice(ClutElementHeaderLength + GridPointsLength);
            for (int i = 0; i < NodeCount; i++)
            {
                int bits = BitConverter.ToInt32(BitConverter.GetBytes((float)i), 0);
                BinaryPrimitives.WriteInt32BigEndian(values.Slice(i * sizeof(float)), bits);
            }

            var profile = new IccProfile(data);

            IccMultiProcessElementsTagDataEntry entry = Assert.IsType<IccMultiProcessElementsTagDataEntry>(Assert.Single(profile.Entries));
            IccClutProcessElement clutElement = Assert.IsType<IccClutProcessElement>(Assert.Single(entry.Data));
            Assert.Equal(gridPointCount, clutElement.ClutValue.GridPointCount);
            Assert.Equal(NodeCount, clutElement.ClutValue.Values.Length);
            for (int i = 0; i < NodeCount; i++)
            {
                Assert.Equal(new[] { (float)i }, clutElement.ClutValue.Values[i]);
            }
        }

        private IccReader CreateReader()
        {
            return new IccReader();
        }
    }
}
