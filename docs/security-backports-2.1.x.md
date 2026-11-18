# Security backports for the 2.1.x line

This fork tracks the last Apache-2.0 ImageSharp line (`release/2.1.x`, v2.1.13 + 6 commits,
tip `14efb84e8`). From v3.0.0 upstream moved to the Six Labors Split License. Upstream published
several 2026 advisories whose affected ranges include 2.x but shipped fixes only in 3.2.0 and
4.1.2. This document specifies how those fixes are ported to 2.1.x.

## Decision and evidence

- License: `LICENSE` at `v2.1.13` is Apache 2.0; at `v3.0.0` it is the Six Labors Split License.
- Advisory list: `gh api repos/SixLabors/ImageSharp/security-advisories` and the global
  advisory database (`gh api advisories?ecosystem=nuget&affects=SixLabors.ImageSharp`), read
  2026-11-18. Both list the same 16 advisories.
- Reference fixes: upstream `release/3.2.x` backport commits (tag `v3.2.0`), read with
  `git show <sha>`. Each is a backport of PR #3187 to v3.
- Not read: upstream v4 sources beyond the advisory text, any private PoC attachments.

### Advisory triage for 2.1.x

| Advisory | Summary | 2.1.x status | Action |
|---|---|---|---|
| GHSA-65x7-c272-7g7r | Use after free | Fixed upstream in 2.1.7 | none |
| GHSA-5x7m-6737-26cr | Buffers not cleared | Fixed upstream in 2.1.8 | none |
| GHSA-g85r-6x2q-45w7 | Excessive allocation | Fixed upstream in 2.1.8 | none |
| GHSA-qxrv-gp6x-rc23 | GIF allocation | Fixed upstream in 2.1.9 | none |
| GHSA-63p8-c4ww-9cg7 | OOB write | Fixed upstream in 2.1.9 | none |
| GHSA-2cmq-823j-5qj8 | OOB write | Fixed upstream in 2.1.10 | none |
| GHSA-rxmq-m78w-7wmc | GIF infinite loop | Fixed upstream in 2.1.11 | none |
| GHSA-gwg2-r3hj-4w44 | ICC CLUT allocation from unvalidated dimensions | Affected, unpatched | port |
| GHSA-j3p4-wp97-rph4 | HistogramEqualization unchecked luminance index | Affected, unpatched (AccessViolation reproduced) | port |
| GHSA-wmxv-xphr-5c9g | BigTIFF IFD count non-progressing loop | Affected, unpatched (5e9 iterations from 24 bytes reproduced) | port |
| GHSA-j9gm-c75j-xc9q | CCITT T4 encoder undersized output buffer | Affected, unpatched (IndexOutOfRange on 2.1, span is bounds-checked) | port |
| GHSA-jjfr-hcj7-qf5w | CCITT T6 encoder undersized output buffer | Affected, unpatched (IndexOutOfRange on 2.1) | port |
| GHSA-jj3q-cwqj-842r | CCITT T4/MH decoder unbounded WriteBits | Listed `>= 3.0.0`. 2.1 `BitWriterUtils` indexes a `Span`, so writes are bounds-checked | verify not affected |
| GHSA-v76p-62qx-wwq2 | Tiled fax TIFF width mismatch | Listed `>= 3.0.0`. 2.1 has no tiled TIFF decoding | verify not affected |
| GHSA-ffp7-56pq-64mr | ICC LUT16 Vector4 overflow | v4 only (ICC conversion) | none |
| GHSA-4q3p-rj5x-xv7p | EXR ZIP stale data | v4 only (no EXR in 2.1) | none |

The related closed report GHSA-f77j-67h2-c87w (undersized ICC tags) is ported as hardening
because upstream shipped it in the same backport set.

## Source map

| Upstream commit (v3.2.0) | Advisory | 2.1.x target files |
|---|---|---|
| `28ade811d` Backport ICC CLUT allocation validation | GHSA-gwg2 | `src/ImageSharp/Metadata/Profiles/ICC/DataReader/IccDataReader.Lut.cs`, `src/ImageSharp/Metadata/Profiles/ICC/Various/IccClut.cs` (fork-only, see Deviations), `tests/ImageSharp.Tests/Metadata/Profiles/ICC/DataReader/IccDataReaderLutTests.cs`, `tests/ImageSharp.Tests/Metadata/Profiles/ICC/IccReaderTests.cs` |
| `d260ca32d` Backport undersized ICC tag rejection | GHSA-f77j (closed) | `src/ImageSharp/Metadata/Profiles/ICC/IccReader.cs`, `tests/ImageSharp.Tests/Metadata/Profiles/ICC/IccReaderTests.cs` |
| `5d9bf4165` Backport BigTIFF directory count validation | GHSA-wmxv | `src/ImageSharp/Metadata/Profiles/Exif/ExifReader.cs`, `tests/ImageSharp.Tests/Formats/Tiff/BigTiffDecoderTests.cs` |
| `1ce37381a` Backport histogram luminance bounds | GHSA-j3p4 | `src/ImageSharp/Common/Helpers/ColorNumerics.cs`, `tests/ImageSharp.Tests/Processing/Normalization/HistogramEqualizationTests.cs` |
| `cb7c4c1aa` Backport CCITT T4 output buffer sizing | GHSA-j9gm | `src/ImageSharp/Memory/InvalidMemoryOperationException.cs`, `src/ImageSharp/Formats/Tiff/Compression/Compressors/TiffCcittCompressor.cs`, `.../T4BitCompressor.cs`, `tests/ImageSharp.Tests/Formats/Tiff/TiffEncoderTests.cs`, `tests/ImageSharp.Tests/Formats/Tiff/Compression/CcittTiffCompressionTests.cs` |
| `12cd3cc46` Backport CCITT T6 output buffer sizing | GHSA-jjfr | `.../TiffCcittCompressor.cs`, `.../T6BitCompressor.cs`, `tests/ImageSharp.Tests/Formats/Tiff/TiffEncoderTests.cs` |

Upstream commits do not cherry-pick: v3 uses file-scoped namespaces and 2.1 uses block-scoped
namespaces, so every line differs in indentation. Each is ported by hand.

## Contracts

No public API changes. `TiffCcittCompressor` and its subclasses, `IccDataReader`, `IccReader`,
`ExifReader` and `ColorNumerics` are internal. Observable behaviour changes:

- Malformed ICC CLUT data throws `InvalidIccProfileException` before allocating. `IccReader`
  already drops tags whose parse throws, so `IccProfile.Entries` omits the tag.
- ICC tag table entries with `tagSize < 8` or that extend past the profile end are skipped.
- ICC CLUTs whose grid point counts differ per input channel (for example in `mAB `, `mBA ` and
  `mpet` tags) validate against the product of the grid points, so these tags now load where
  they were previously dropped.
- A BigTIFF IFD that is truncated (declared entries, or the 8-byte next-IFD offset after them,
  extend beyond the end of the stream) now fails `Image.Load` with
  `InvalidImageContentException`. Such files could previously load.
- `GetBT709Luminance` returns 0 for NaN and clamps luminance to [0, 1] before scaling, so
  histogram indices stay in `[0, luminanceLevels - 1]`.
- TGA 8-bit luminance output (`TgaEncoderCore.GetLuminance`) shares this helper, so
  out-of-range floating-point samples now saturate to 0 or 255 instead of wrapping.
- CCITT T4/T6 encoders size their output buffer from a per-codec upper bound and throw
  `InvalidMemoryOperationException` instead of writing past it. A bound above `int.MaxValue`
  throws from `Initialize` with the allocator's message
  (`Attempted to allocate a buffer of length=... that exceeded the limit 2147483647.`).

## Per-unit specs

### ICC CLUT (GHSA-gwg2)
- Add private `GetClutLength(inputChannelCount, outputChannelCount, gridPointCount, bytesPerValue)`
  to `IccDataReader`. Node count is the product of grid points per input channel. Throw
  `InvalidIccProfileException("Invalid CLUT dimensions.")` for a zero grid point or int overflow.
  Throw `InvalidIccProfileException("The CLUT data is shorter than its declared dimensions.")`
  when `length * outputChannelCount` exceeds `int.MaxValue` or the payload
  (`* bytesPerValue`) exceeds the bytes remaining after `currentIndex`.
- `ReadClut8` (1 byte), `ReadClut16` (2), `ReadClutF32` (4) call it instead of the
  `Math.Pow` sum. Keep the 2.1 jagged `float[][]` storage.
- `IccClut.CheckValues` validates `Values.Length` against the same product of grid points
  (fork-only, see Deviations).

### ICC tag table (GHSA-f77j hardening)
- In `IccReader.ReadTagTable`, accept an entry only when `tagSize >= 8 && tagOffset <= dataLength
  && tagSize <= dataLength - tagOffset`, with `dataLength = (uint)reader.DataLength`.

### BigTIFF IFD count (GHSA-wmxv)
- In `ExifReader.ReadValues64`, after reading the 64-bit count, require
  `remaining >= 8` and `count <= (remaining - 8) / 20`, where `remaining` is stream length minus
  position, 20 is the BigTIFF entry length and 8 the next-IFD offset length. Otherwise throw
  `InvalidImageContentException("The BigTIFF directory entry count exceeds the available data.")`.

### Histogram luminance (GHSA-j3p4)
- `ColorNumerics.GetBT709Luminance`: NaN -> 0; clamp to [0, 1] with `Numerics.Clamp`, then
  `(int)MathF.Round(luminance * (luminanceLevels - 1))`. Keep the `ref Vector4` signature.
  This covers every caller in 2.1: global and adaptive histogram equalization
  (`GlobalHistogramEqualizationProcessor`, `AdaptiveHistogramEqualizationProcessor`,
  `AdaptiveHistogramEqualizationSlidingWindowProcessor`) and `TgaEncoderCore.GetLuminance`.

### CCITT encoders (GHSA-j9gm, GHSA-jjfr)
- `TiffCcittCompressor.WriteCode`: before writing, throw
  `InvalidMemoryOperationException("The CCITT output buffer is too small for the encoded data.")`
  when `codeLength` exceeds the remaining bits in `compressedData`.
- `TiffCcittCompressor.Initialize`: allocate `ceil(GetMaximumEncodedBits(rowsPerStrip) / 8)`
  bytes as `ulong maxNeededBytes`; when that exceeds `int.MaxValue`, call
  `InvalidMemoryOperationException.ThrowAllocationOverLimitException(maxNeededBytes, int.MaxValue)`.
- `InvalidMemoryOperationException`: add upstream's internal
  `ThrowAllocationOverLimitException(ulong length, long limit)`, whose message matches the 2.1
  allocators' over-limit message.
- New `protected abstract long GetMaximumEncodedBits(int rowsPerStrip)`.
- `T4BitCompressor`: `12 + ((Width * 13 + 20) * rowsPerStrip)` (initial EOL; 13-bit terminating
  code per pixel; 8-bit zero white run + 12-bit EOL per row). Covers Modified Huffman (Ccitt1D),
  which shares this class and pads to a byte instead of writing an EOL.
- `T6BitCompressor`: `((Width * 16 + 24) * rowsPerStrip) + 24` (16 bits per pixel, row
  transition overhead, 24-bit EOFB).

## Removals

- The `Math.Pow`-sum CLUT length computation in the three `ReadClut*` methods and in
  `IccClut.CheckValues`.
- The `Width * rowsPerStrip` CCITT allocation and its comment in `TiffCcittCompressor.Initialize`.

## Tests

"Pre-fix result" is the result on the unfixed base `14efb84e8` with the test files copied in.

| Test | File | Pre-fix result on 2.1 |
|---|---|---|
| `ReadClut_WithOversizedDimensions_ThrowsInvalidIccProfileException` | `IccDataReaderLutTests.cs` | fails |
| `ReadClut_WithZeroGridPoints_ThrowsInvalidIccProfileException` | `IccDataReaderLutTests.cs` | fails |
| `ReadClut8_WithUnequalGridPoints_ReadsProductOfGridPoints` (grid points 2, 3, 4) | `IccDataReaderLutTests.cs` | fails (`IndexOutOfRangeException`) |
| `ReadProfile_WithUndersizedArrayTags_IgnoresTags` | `IccReaderTests.cs` | fails on .NET Core targets: the allocation assertion reports about 430 GB allocated. The assertion uses `GC.GetAllocatedBytesForCurrentThread` and is compiled only for `NETCOREAPP`; on net472 the test checks only that the tags are dropped |
| `ReadProfile_WithUnequalClutGridPoints_ReadsMultiProcessElementsTag` | `IccReaderTests.cs` | fails (tag dropped) |
| `TiffDecoder_DirectoryEntryCountExceedsAvailableData_Throws` | `BigTiffDecoderTests.cs` | fails after an 11 s loop |
| `TiffDecoder_DirectoryEntriesLeaveTooLittleDataForNextIfdOffset_Throws` (0, 4, 7 bytes of next-IFD offset) | `BigTiffDecoderTests.cs` | fails (image loads) |
| `TiffDecoder_DirectoryWithCompleteNextIfdOffset_Decodes` | `BigTiffDecoderTests.cs` | passes (control for the test above) |
| `GlobalHistogramEqualization_ClampsDecodedFloatingPointSamples` (2, -1, +inf, -inf, NaN) | `HistogramEqualizationTests.cs` | every case fails; +inf crashes the test host (`AccessViolationException`), the others produce wrong equalized values |
| `GlobalHistogramEqualization_ClampsDecodedRgbSamplesWithNonFiniteChannels` (R = +inf; R = NaN; R = +inf and G = -inf) | `HistogramEqualizationTests.cs` | every case fails; R = +inf crashes the test host |
| `TiffEncoder_EncodeNarrowCcittGroup3Fax_Works` (width 1, 64) | `TiffEncoderTests.cs` | width 1 fails |
| `TiffEncoder_EncodeNarrowAlternatingCcitt_Works` (G4, 1D x width 1, 64) | `TiffEncoderTests.cs` | 1D width 1 fails |
| `TiffEncoder_ReencodeNarrowCcittGroup4Fax_Works` | `TiffEncoderTests.cs` | fails |
| `TiffEncoder_EncodeRandomRunsCcitt_RoundTrips` (G3, G4, 1D x 23 widths) | `TiffEncoderTests.cs` | G3 width 1 and 2 and 1D width 1 fail |
| `CompressStrip_WithMoreRowsThanInitialized_ThrowsInvalidMemoryOperationException` (G3, G4, 1D) | `CcittTiffCompressionTests.cs` | fails (`IndexOutOfRangeException`) |
| `Initialize_WithEncodedBoundOverInt32_ThrowsWithoutAllocating` (G3, G4, 1D) | `CcittTiffCompressionTests.cs` | fails (`OverflowException`) |

## Deliberate deviations from upstream

- No new code comments (fork convention): upstream's explanatory comments are replaced with named
  constants (`BigTiffEntryLength`, `EndOfLineBits`, `MaxBitsPerPixel`, ...). XML docs on the
  new protected abstract member are kept.
- `Numerics.Clamp` instead of `Math.Clamp` (2.1 targets netstandard2.0 and net472).
- `InvalidMemoryOperationException.ThrowAllocationOverLimitException` does not exist in 2.1 and
  is added from upstream. Its `[DoesNotReturn]` attribute is compiled only for netstandard2.1
  and netcoreapp3.1 or later, because the attribute is not public on netstandard2.0, net472 and
  netcoreapp2.1. Only this helper is added; upstream's other throw helpers are not needed.
- `IccClut.CheckValues` validates the node count as the product of grid points instead of the
  `Math.Pow` sum. Upstream v3.2.0 still uses the `Math.Pow` sum there, which disagrees with the
  ported `GetClutLength` whenever grid points differ per channel: such CLUTs would fail
  validation after the port and their tags would be silently dropped. The fix goes beyond
  upstream.
- Collection expressions and range indexers in upstream tests are rewritten for 2.1's language
  version and frameworks (array initializers, `Span.Slice`).
- Upstream's histogram test asserts only the image size. Here it uses four in-range and four
  out-of-range samples and asserts the equalized values, so out-of-range samples must land in
  the top bin (above 1) or the bottom bin (below 0, NaN). `BuildFloatTiff` keeps upstream's
  text but takes the samples as an array.
- Upstream's undersized-tag test gains an allocation assertion on .NET Core targets.
- Extra test cases beyond upstream: negative/infinite/NaN histogram samples, RGB samples with a
  single non-finite channel, a zero-grid-point CLUT, CLUTs with unequal grid points, BigTIFF
  IFDs with a truncated next-IFD offset, direct `TiffCcittCompressor` tests for the `WriteCode`
  bound and the `Initialize` limit, Group 4 / Modified Huffman narrow-image round trips, and
  `TiffEncoder_EncodeRandomRunsCcitt_RoundTrips` (fixed-seed random runs, including makeup-code
  lengths, for widths 1..17, 63..65, 255, 2560 and 2561 under all three CCITT codecs). Its
  Modified Huffman images end with an all-white row because of the decoder limitation under
  Residuals; Group 3 and Group 4 images round-trip their random last row.

## Verification plan

- Library with no runnable host: the xUnit suite on net6.0 (`SIXLABORS_TESTING_PREVIEW=true`).
- Compile `src/ImageSharp` for every target framework.
- Full suite on the fixed branch and on unmodified `release/2.1.x`; the failure sets must match.
- Tests that depend on reference decoders (ImageMagick, System.Drawing) are covered by the
  repository CI matrix.
- Each new test is run against the unfixed base with the test files copied in; the expected
  pre-fix results are in the Tests table.
- Advisory PoCs: craft the reported inputs and confirm the fixed build rejects them with a
  managed exception, and that GHSA-jj3q / GHSA-v76p inputs raise a managed exception (no process
  crash) on 2.1.

## Residuals

- Packaging: no NuGet package is produced from this fork yet; versioning (MinVer from tags)
  and feed are open.
- GHSA-jj3q / GHSA-v76p on 2.1 raise `IndexOutOfRangeException` rather than
  `InvalidImageContentException` for malformed fax strips. Not memory-unsafe; left as is.
- Modified Huffman decoding (`ModifiedHuffmanBitReader.HasMoreData`, unchanged in upstream
  v3.2.0) stops when the strip's last byte starts on a code boundary, so a final row encoded
  entirely in that byte is not decoded and keeps the zero-filled buffer value (white for the
  encoder's default `WhiteIsZero` output). Data loss only, not
  memory-unsafe, and not introduced by these ports; left as is.
  - The rule applies per strip, so in a multi-strip image the lost row can be an interior row
    of the image (for example row 8191 of a 4-pixel-wide, 20000-row image, which the encoder
    writes as 8192-row strips).
  - The lost row is replaced by white, so when it starts with white pixels the first
    differing pixel is not at x = 0. Mismatches seen for Modified Huffman at width 4 were all
    of this kind: a whole row left white.
  - The encoder output is correct: libtiff decodes the same files without mismatches. Both
    observations reproduce on the unfixed base. No separate encoder or decoder bug was found.
