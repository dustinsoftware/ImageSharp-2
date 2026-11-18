// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;

namespace SixLabors.ImageSharp.Tests.TestUtilities
{
    /// <summary>
    /// A read-only, seekable <see cref="Stream"/> that reports a given length, returns the bytes of a prefix
    /// first and zeros after it.
    /// </summary>
    internal sealed class LargeLengthStream : Stream
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
}
