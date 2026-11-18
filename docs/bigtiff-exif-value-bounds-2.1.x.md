# BigTIFF IFD entry value bounds (2.1.x)

## Problem

`BaseExifReader.ReadValue64` (src/ImageSharp/Metadata/Profiles/Exif/ExifReader.cs, lines 477-488 at 14efb84e8)
validates an out-of-line BigTIFF entry value with:

```csharp
ulong size = numberOfComponents * ExifDataTypes.GetSize(dataType);
if (size > 8)
{
    ulong newOffset = this.ConvertToUInt64(offsetBuffer);
    if (newOffset > ulong.MaxValue || newOffset > ((ulong)this.data.Length - size))
```

1. When `size > data.Length` the unsigned subtraction `data.Length - size` wraps to a value near
   `ulong.MaxValue`, so any small offset passes and the entry is queued in `BigValues`.
   `newOffset > ulong.MaxValue` is always false.
2. `ReadBigValues` (lines 126-168) then computes `maxSize` from `(int)size` and calls
   `this.allocator.Allocate<byte>(maxSize)`. A 233-byte file therefore requests up to 1 GiB
   (the default single-buffer cap); larger sizes raise `InvalidMemoryOperationException`
   (wrapped in `InvalidImageContentException`), and under memory pressure `OutOfMemoryException`.
3. The only size check before the `(int)size` casts is a `DebugGuard` (line 137), compiled out of
   Release. A size of `2^31` casts to `int.MinValue`, giving `Allocate<byte>(0)` and an
   `ArgumentOutOfRangeException` from `Span.Slice`. A size of `2^32 + 16` casts to 16 and a
   16-byte value is silently read for an entry that declared 4 GiB.
4. `numberOfComponents` is a file-controlled `ulong`; `numberOfComponents * elementSize`
   (element size 1, 2, 4 or 8) can wrap, producing a small size that disagrees with the declared
   count. A wrapped size of 8 or less goes down the inline branch; a wrapped size above 8 is
   queued as an external read shorter than declared.

Reachable through public `Image.Identify` and `Image.Load` for any BigTIFF input.

## Invariant

Immediately before `this.BigValues.Add(...)` in `ReadValue64`:

```
8 < size == numberOfComponents * elementSize   (exact; the product did not wrap)
size <= int.MaxValue
size <= data.Length
newOffset + size <= data.Length                (evaluated as newOffset <= data.Length - size,
                                                which cannot underflow because size <= data.Length)
```

So every queued value lies entirely within the stream, `(int)size` in `ReadBigValues` is exact
and non-negative, and the buffer `ReadBigValues` requests is never larger than the stream.

## Change (ReadValue64 only)

```csharp
ulong elementSize = ExifDataTypes.GetSize(dataType);
if (numberOfComponents > ulong.MaxValue / elementSize)
{
    this.AddInvalidTag(new UnkownExifTag(tag));
    return;
}

ulong size = numberOfComponents * elementSize;
if (size > 8)
{
    ulong newOffset = this.ConvertToUInt64(offsetBuffer);
    ulong length = (ulong)this.data.Length;
    if (size > int.MaxValue || size > length || newOffset > length - size)
    {
        this.AddInvalidTag(new UnkownExifTag(tag));
        return;
    }

    this.BigValues.Add(...);
}
```

The overflow guard runs before the inline/external branch. A count whose product wraps cannot
describe a real value (it would need more than 2^64 bytes), and letting a wrapped product of 8 or
less reach the inline branch would decode a value whose element count disagrees with the declared
count. Placing the guard first gives both branches an exact size. No valid file is affected,
because every valid value fits either in the 8-byte inline field or inside the stream.

`ReadValue` (classic TIFF), `ReadValues64` and `ReadBigValues` are unchanged: the invariant
makes the existing `ReadBigValues` arithmetic safe without touching it.

Malformed entries use the existing behaviour: the tag is added to `InvalidTags` and dropped.
`Identify` and `Load` continue with the remaining tags.

## Tests

New file `tests/ImageSharp.Tests/Formats/Tiff/BigTiffMetadataBoundsTests.cs`. BigTIFF bytes
(1x1, 8-bit grayscale, uncompressed) are built in-test. Every test uses a `Configuration` whose
allocator records each request and throws for any request above 1 MiB, so an unfixed build
cannot allocate large buffers during the tests.

Unless noted, each test calls `Image.Identify` (asserts 1x1) and `Image.Load`, then asserts on
`image.Frames.RootFrame.Metadata.ExifProfile` that the malformed tag is absent from `Values`, is
listed in `InvalidTags`, that the valid inline `Software` tag is still present, and that no
allocator request was as large as the stream. In 2.1.x `Identify` does not expose the EXIF profile,
so the profile assertions use `Load`.

| Test | Cases | Observed at base commit (fix reverted in a copy) |
|---|---|---|
| ValidMetadata_IsRead | LE, BE | Pass. Also asserts inline XResolution via Identify and inline Software, external ImageDescription and Artist via Load |
| ValidExternalValueEndingAtEndOfStream_IsRead | offset + size == length | Pass |
| ValueEndingOneBytePastEndOfStream_IsDropped | offset + size == length + 1 | Pass |
| ValueOneByteLargerThanStream_IsDroppedWithoutAllocating | size == length + 1, offset 16 | Fail: tag not in InvalidTags (queued, then a short read) |
| ValueLargerThanStream_IsDroppedWithoutAllocating | 256 MiB, 1 GiB (LE, BE), int.MaxValue | Fail: allocator request of 256 MiB / 1 GiB / int.MaxValue bytes |
| | int.MaxValue + 1 (LE, BE), ulong.MaxValue | Fail: ArgumentOutOfRangeException |
| | 2^32 + 16 | Fail: tag present (16-byte misread) |
| ValueLargerThanIntMaxValueInsideLargeStream_IsDropped | 2^31, 2^32 + 2^31 with a seekable stream reporting 8 GiB | Fail: ArgumentOutOfRangeException |
| | 2^32 + 16 | Fail: tag present |
| OffsetOutsideStream_IsDropped | offset ulong.MaxValue size 9; ulong.MaxValue - 4 size 16; long.MaxValue size 16 | Pass |
| OffsetAtEndOfStream_IsDropped | offset == length, size 9 | Pass |
| ComponentCountOverflow_IsDropped | Rational 2^61 + 1, 2^61 + 2, 2^61; Short 2^63 + 1; Long 2^62 + 1, 2^62 + 4 | Fail: tag present (all six) |

Totals: 26 cases; at base 18 fail and 8 pass; with the fix 26 pass. The ComponentCountOverflow
case `Rational 2^61` wraps to size 0; at base it takes the inline branch and is added to `Values`
even though no value bytes were decoded.

## Limitations

- A BigTIFF metadata value larger than `int.MaxValue` bytes is rejected even if the stream is
  that large. `ReadBigValues` reads into a single `int`-indexed span, so it could not be read anyway.
- Values up to `min(stream length, int.MaxValue)` are still accepted and allocated as one buffer,
  so a large legitimate stream can still request up to that size (subject to the allocator's own
  single-buffer limit, which is unchanged).
- Classic TIFF (`ReadValue`) is out of scope here and is being assessed separately.
- `ReadValues64` iterates a file-controlled `ulong` entry count; past end of stream each iteration
  returns immediately but the loop continues. That is a CPU concern, not part of this change.

## Verification

- `dotnet build src/ImageSharp/ImageSharp.csproj -c Release` for all target frameworks.
- `SIXLABORS_TESTING_PREVIEW=true DOTNET_ROLL_FORWARD=Major dotnet test tests/ImageSharp.Tests/ImageSharp.Tests.csproj -c Release -f net6.0 --filter "FullyQualifiedName~Tiff"`.
- Run the new test file against a copy of the tree with the ExifReader change reverted to confirm the
  expected base-commit results above.
