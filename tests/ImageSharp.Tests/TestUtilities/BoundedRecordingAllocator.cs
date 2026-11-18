// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.Memory;

namespace SixLabors.ImageSharp.Tests.TestUtilities
{
    /// <summary>
    /// A <see cref="MemoryAllocator"/> that records the size of every request in bytes and throws for any request
    /// larger than 1 MiB.
    /// </summary>
    internal sealed class BoundedRecordingAllocator : MemoryAllocator
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
}
