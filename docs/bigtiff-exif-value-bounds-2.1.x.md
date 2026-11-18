# TIFF / EXIF IFD entry value bounds (2.1.x)

This covers both IFD entry readers in `BaseExifReader`
(src/ImageSharp/Metadata/Profiles/Exif/ExifReader.cs): `ReadValue64` (BigTIFF) and `ReadValue`
(classic TIFF and every embedded EXIF profile: JPEG APP1, PNG eXIf, WebP EXIF).

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

`ReadValues64` and `ReadBigValues` are unchanged: the invariant makes the existing `ReadBigValues`
arithmetic safe without touching it. `ReadValue` is covered in the next section.

Malformed entries use the existing behaviour: the tag is added to `InvalidTags` and dropped.
`Identify` and `Load` continue with the remaining tags.

## Classic TIFF and embedded EXIF (ReadValue)

### Problem

`BaseExifReader.ReadValue` (lines 401-413 at c01f126e4) is the 32-bit twin of `ReadValue64`:

```csharp
uint size = numberOfComponents * ExifDataTypes.GetSize(dataType);
if (size > 4)
{
    uint newOffset = this.ConvertToUInt32(offsetBuffer);
    if (newOffset > int.MaxValue || (newOffset + size) > this.data.Length)
```

1. `newOffset + size` is a `uint` addition. With `newOffset <= int.MaxValue` and
   `size >= 2^32 - newOffset` it wraps to a small value, the check passes and the entry is queued.
   Example: an ASCII count of `0xFFFFFFF0` at offset `0x20` wraps to 16.
2. Every size that can wrap this way is above `int.MaxValue`. `ReadBigValues` casts it with
   `(int)size`, gets a negative number, keeps `maxSize` at its previous value (0 if alone) and
   `buf.Slice(0, (int)size)` throws `ArgumentOutOfRangeException`. The `DebugGuard` that would
   catch this is compiled out of Release.
3. `numberOfComponents * elementSize` is a `uint` multiply and can wrap. A Long count of
   `0x40000001` gives size 4 and is decoded inline as a single value; `0x40000002` gives size 8 and
   reads 8 bytes from the stream for an entry that declared 4 GiB.

No large allocation happens on this path (a wrapped entry's size is negative after the `(int)`
cast, and an entry whose sum did not wrap is bounded by the stream length), so the impact is an unhandled
`ArgumentOutOfRangeException` from public `Image.Identify` / `Image.Load` plus silently misread
values. Reached through:

- Classic TIFF: `Image.Identify` and `Image.Load` (135-byte reproducer).
- JPEG APP1 EXIF: `Image.Identify` parses the profile eagerly when there is EXIF but no JFIF
  (`JpegDecoderCore.InitDerivedMetadataProperties` -> `GetExifResolutionValue` ->
  `ExifProfile.GetValue`), with a null allocator, so the same exception escapes `Identify` for a
  188-byte file. PNG eXIf and WebP EXIF construct `ExifProfile` from the same bytes and parse them
  with the same `ExifReader` on first access.

### Invariant

Immediately before `this.BigValues.Add(...)` in `ReadValue`:

```
4 < size == numberOfComponents * elementSize   (exact; computed in 64 bits, at most
                                                (2^32 - 1) * 8 < 2^35, so it cannot wrap)
newOffset <= int.MaxValue                      (unchanged pre-existing limit)
size <= int.MaxValue
newOffset + size <= data.Length                (computed in 64 bits; both terms are at most
                                                int.MaxValue here, so the sum cannot wrap)
```

This is the invariant `ReadValue64` establishes (`size <= data.Length` follows from the last
line), plus the existing classic-TIFF limit on the offset, so every queued value from either reader lies inside the stream and `(int)size` in
`ReadBigValues` is exact and non-negative. On the inline branch `size <= 4` is exact, so the
`(int)size` slice of the 4-byte offset field is always in range.

### Change (ReadValue only)

```csharp
ulong size = (ulong)numberOfComponents * ExifDataTypes.GetSize(dataType);
if (size > 4)
{
    uint newOffset = this.ConvertToUInt32(offsetBuffer);

    // Ensure that the new index does not overrun the data.
    if (newOffset > int.MaxValue || size > int.MaxValue || newOffset + size > (ulong)this.data.Length)
    {
        this.AddInvalidTag(new UnkownExifTag(tag));
        return;
    }

    this.BigValues.Add((newOffset, dataType, numberOfComponents, exifValue));
}
```

`numberOfComponents` is a `uint` and the element size is at most 8, so widening to `ulong` before
the multiply makes the product exact; no separate overflow guard (as in `ReadValue64`) is needed.
Because the size is exact before the inline/external branch, a count whose 32-bit product used to
wrap to 4 or less now takes the external branch and is rejected by `size > int.MaxValue`. The
`numberOfComponents == 0` fallback is unchanged: it still yields a size of 4 (or 0 for 8-byte
types) and an inline decode.

`size > int.MaxValue` is required on top of the stream check for streams larger than 2 GiB:
without it a classic TIFF on such a stream could queue a value whose `(int)size` is negative.

The existing `newOffset > int.MaxValue` clause is kept so that the set of accepted entries only
shrinks: every entry accepted after the change was accepted before, and the only entries now
rejected are the malformed ones above. It is not what makes the path safe (it never prevented the
wrap: any offset up to `int.MaxValue` combined with a large enough size still wrapped); the size
and stream checks are. Keeping it preserves the current behaviour for a classic TIFF stream larger
than 2 GiB, where an out-of-line value stored at an offset past `int.MaxValue` is still listed in
`InvalidTags`. `ReadValue64` has no such clause and is unchanged.

Malformed entries use the existing behaviour (`InvalidTags`, tag dropped, decoding continues).
`ReadValues64`, `ReadValue64` and `ReadBigValues` are not changed by this step.

## Tests

### BigTIFF (ReadValue64)

New file `tests/ImageSharp.Tests/Formats/Tiff/BigTiffMetadataBoundsTests.cs`. BigTIFF bytes
(1x1, 8-bit grayscale, uncompressed) are built in-test. Every test uses a `Configuration` whose
allocator records each request and throws for any request above 1 MiB, so an unfixed build
cannot allocate large buffers during the tests.

Unless noted, each test calls `Image.Identify` (asserts 1x1) and `Image.Load`, then asserts on
`image.Frames.RootFrame.Metadata.ExifProfile` that the malformed tag is absent from `Values`, is
listed in `InvalidTags`, that the valid inline `Software` tag is still present, and that no
allocator request was as large as the stream. In 2.1.x TIFF `Identify` does not expose the EXIF profile,
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

### Classic TIFF and embedded EXIF (ReadValue)

New file `tests/ImageSharp.Tests/Formats/Tiff/TiffMetadataBoundsTests.cs`. Classic TIFF bytes
(1x1, 8-bit grayscale, uncompressed, external XResolution/YResolution, inline 4-byte `Software`
"TIF") are built in-test and read with the same recording allocator (throws above 1 MiB). The
malformed-entry tests assert, after `Identify` (1x1) and `Load`, that the tag is absent from
`Values`, listed in `InvalidTags`, the inline `Software` and the out-of-line `XResolution` are
still present, and no allocator request was as large as the stream.

New file `tests/ImageSharp.Tests/Metadata/Profiles/Exif/ExifValueBoundsTests.cs` covers embedded
EXIF: a minimal grayscale baseline JPEG (SOI, APP1 "Exif\0\0" + TIFF header/IFD, DQT, SOF0, two
DHT, SOS, EOI) through `Image.Identify` / `Image.Load` (asserting on `Metadata.ExifProfile`,
which 2.1.x exposes for JPEG from both), and raw payloads through the public
`new ExifProfile(byte[])` (the path PNG eXIf and WebP EXIF use). The EXIF payload carries an
inline `Software` "JPG" before the entry under test and, in the malformed-entry tests, a valid
out-of-line `Artist` after it; both must still be read.

Base = c01f126e4 (BigTIFF fix present, `ReadValue` unchanged) with the new test files added.

| Test | Cases | Observed at base | With fix |
|---|---|---|---|
| ValidMetadata_IsRead | LE, BE: inline Software (4 bytes), inline PageNumber Short x2 (4 bytes), external ImageDescription, Artist, X/YResolution | Pass | Pass |
| ValidExternalValueEndingAtEndOfStream_IsRead | offset + size == length | Pass | Pass |
| ZeroComponentCount_FallsBackToInlineValue | Undefined, count 0 -> 4 inline bytes | Pass | Pass |
| ValueEndingOneBytePastEndOfStream_IsDropped | offset + size == length + 1 | Pass | Pass |
| ValueOneByteLargerThanStream_IsDropped | size == length + 1, offset 16 | Pass | Pass |
| OffsetPlusSizeWraps_IsDropped | Ascii 0xFFFFFFF0 @ 0x20 (LE, BE); Ascii 0x80000001 @ 0x7FFFFFFF; Short 0x7FFFFFF8, Long 0x3FFFFFFC, Rational 0x1FFFFFFE, Double 0x1FFFFFFE @ 0x20; Long 0x20000001 @ 0x7FFFFFFC | Fail (8/8): ArgumentOutOfRangeException | Pass |
| | Ascii 0x7FFFFFFF @ 0x80000001 (LE, BE); Long 0x1FFFFFFF @ 0x80000004 (size <= int.MaxValue, 32-bit sum wraps to 0) | Pass (`newOffset > int.MaxValue`) | Pass (same clause, kept) |
| ValueOutsideStream_IsDropped | Ascii 0x80000000 @ 16; 0xFFFFFFFF @ 0; 5 @ 0xFFFFFFFF; 5 @ 0x80000000 | Pass | Pass |
| ValueLargerThanIntMaxValueInsideLargeStream_IsDropped | stream reporting 8 GiB, offset 16: Ascii 2^31, 2^32 - 1; Short 2^30; Long 2^30 - 1; Rational 2^28; Double 2^29 - 1 | Fail (6/6): ArgumentOutOfRangeException | Pass |
| ValueBeyondIntMaxValueOffsetInsideLargeStream_IsDropped | stream reporting 8 GiB, Ascii 8 @ 0x80000000 (pins the unchanged offset limit) | Pass | Pass |
| ComponentCountOverflow_IsDropped | Long 0x40000001 (wraps to 4), 0x40000002 (8); Short 0x80000002 (4), 0x80000004 (8); Rational 0x20000000 (0), 0x20000001 (8); Double 0x20000000 (0), 0x20000002 (16) | Fail (8/8): tag present / not in InvalidTags | Pass |
| JpegExifValueWrappingPastEndOfData_IsDropped | JPEG, Ascii 0xFFFFFFF0 @ 16, LE and BE; Identify and Load | Fail (2/2): ArgumentOutOfRangeException from Identify | Pass |
| JpegExifValidExternalValue_IsRead | JPEG, external ImageDescription; Identify and Load | Pass | Pass |
| ExternalValueEndingAtEndOfData_IsRead | ExifProfile, value ends at last byte | Pass | Pass |
| ExternalValueEndingOneBytePastEndOfData_IsDropped | ExifProfile, offset + size == length + 1 | Pass | Pass |
| MalformedExternalValue_IsDropped | ExifProfile: Ascii 0xFFFFFFF0 @ 16, 0x80000001 @ 0x7FFFFFFF; Short 0x7FFFFFF8, Long 0x3FFFFFFC, Rational 0x1FFFFFFE @ 16; Long 0x40000001, 0x40000002; Rational 0x20000000, 0x20000001 | Fail (9/9) | Pass |
| | ExifProfile: Ascii 0x80000000 @ 16 (no wrap); Ascii 0x7FFFFFFF @ 0x80000001; Long 0x1FFFFFFF @ 0x80000004 | Pass | Pass |

Totals: 53 cases (36 TIFF, 17 EXIF); at base 33 fail and 20 pass; with the fix 53 pass. The 26
BigTIFF cases above still pass.

## Limitations

- A BigTIFF metadata value larger than `int.MaxValue` bytes is rejected even if the stream is
  that large. `ReadBigValues` reads into a single `int`-indexed span, so it could not be read anyway.
  The same holds for classic TIFF, whose largest declarable value is `(2^32 - 1) * 8` bytes.
- Classic TIFF and embedded EXIF still reject an out-of-line value whose offset is above
  `int.MaxValue`, even inside a larger stream. That limit predates this change and is kept.
- Values up to `min(stream length, int.MaxValue)` are still accepted and allocated as one buffer,
  so a large legitimate stream can still request up to that size (subject to the allocator's own
  single-buffer limit, which is unchanged).
- Classic TIFF and embedded EXIF (`ReadValue`) were assessed after the BigTIFF change: the same
  wrap existed there and is fixed above. No large allocation was reachable on that path; the
  impact was an unhandled `ArgumentOutOfRangeException` and misread values.
- `ReadValues64` iterates a file-controlled `ulong` entry count; past end of stream each iteration
  returns immediately but the loop continues. That is a CPU concern, not part of this change.

## Verification

- `dotnet build src/ImageSharp/ImageSharp.csproj -c Release` for all target frameworks, with and
  without `SIXLABORS_TESTING_PREVIEW=true`.
- `SIXLABORS_TESTING_PREVIEW=true DOTNET_ROLL_FORWARD=Major dotnet test tests/ImageSharp.Tests/ImageSharp.Tests.csproj -c Release -f net6.0 --filter "FullyQualifiedName~MetadataBounds|FullyQualifiedName~ValueBounds|FullyQualifiedName~Exif|FullyQualifiedName~Tiff|FullyQualifiedName~Jpeg"`.
  Locally, tests that compare against reference images decoded by Magick.NET fail with
  `DllNotFoundException` for Magick.Native in both the base and fixed trees; they are unrelated.
- Run the new test files against a copy of the tree with the relevant ExifReader change reverted to
  confirm the expected base-commit results above.
