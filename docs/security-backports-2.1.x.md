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
| `28ade811d` Backport ICC CLUT allocation validation | GHSA-gwg2 | `src/ImageSharp/Metadata/Profiles/ICC/DataReader/IccDataReader.Lut.cs`, `tests/ImageSharp.Tests/Metadata/Profiles/ICC/DataReader/IccDataReaderLutTests.cs` |
| `d260ca32d` Backport undersized ICC tag rejection | GHSA-f77j (closed) | `src/ImageSharp/Metadata/Profiles/ICC/IccReader.cs`, `tests/ImageSharp.Tests/Metadata/Profiles/ICC/IccReaderTests.cs` |
| `5d9bf4165` Backport BigTIFF directory count validation | GHSA-wmxv | `src/ImageSharp/Metadata/Profiles/Exif/ExifReader.cs`, `tests/ImageSharp.Tests/Formats/Tiff/BigTiffDecoderTests.cs` |
| `1ce37381a` Backport histogram luminance bounds | GHSA-j3p4 | `src/ImageSharp/Common/Helpers/ColorNumerics.cs`, `tests/ImageSharp.Tests/Processing/Normalization/HistogramEqualizationTests.cs` |
| `cb7c4c1aa` Backport CCITT T4 output buffer sizing | GHSA-j9gm | `src/ImageSharp/Formats/Tiff/Compression/Compressors/TiffCcittCompressor.cs`, `.../T4BitCompressor.cs`, `tests/ImageSharp.Tests/Formats/Tiff/TiffEncoderTests.cs` |
| `12cd3cc46` Backport CCITT T6 output buffer sizing | GHSA-jjfr | `.../TiffCcittCompressor.cs`, `.../T6BitCompressor.cs`, `tests/ImageSharp.Tests/Formats/Tiff/TiffEncoderTests.cs` |

Upstream commits do not cherry-pick: v3 uses file-scoped namespaces and 2.1 uses block-scoped
namespaces, so every line differs in indentation. Each is ported by hand.

## Contracts

No public API changes. `TiffCcittCompressor` and its subclasses, `IccDataReader`, `IccReader`,
`ExifReader` and `ColorNumerics` are internal. Observable behaviour changes:

- Malformed ICC CLUT data throws `InvalidIccProfileException` before allocating. `IccReader`
  already drops tags whose parse throws, so `IccProfile.Entries` omits the tag.
- ICC tag table entries with `tagSize < 8` or that extend past the profile end are skipped.
- A BigTIFF IFD whose declared entry count cannot fit in the remaining stream throws
  `InvalidImageContentException`.
- `GetBT709Luminance` returns 0 for NaN and clamps luminance to [0, 1] before scaling, so
  histogram indices stay in `[0, luminanceLevels - 1]`.
- CCITT T4/T6 encoders size their output buffer from a per-codec upper bound and throw
  `InvalidMemoryOperationException` instead of writing past it.

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
  This covers every caller (global and adaptive histogram equalization, AutoLevel).

### CCITT encoders (GHSA-j9gm, GHSA-jjfr)
- `TiffCcittCompressor.WriteCode`: before writing, throw
  `InvalidMemoryOperationException("The CCITT output buffer is too small for the encoded data.")`
  when `codeLength` exceeds the remaining bits in `compressedData`.
- `TiffCcittCompressor.Initialize`: allocate `ceil(GetMaximumEncodedBits(rowsPerStrip) / 8)`
  bytes; throw `InvalidMemoryOperationException` when that exceeds `int.MaxValue`.
- New `protected abstract long GetMaximumEncodedBits(int rowsPerStrip)`.
- `T4BitCompressor`: `12 + ((Width * 13 + 20) * rowsPerStrip)` (initial EOL; 13-bit terminating
  code per pixel; 8-bit zero white run + 12-bit EOL per row). Covers Modified Huffman (Ccitt1D),
  which shares this class and pads to a byte instead of writing an EOL.
- `T6BitCompressor`: `((Width * 16 + 24) * rowsPerStrip) + 24` (16 bits per pixel, row
  transition overhead, 24-bit EOFB).

## Removals

- The `Math.Pow`-sum CLUT length computation in the three `ReadClut*` methods.
- The `Width * rowsPerStrip` CCITT allocation and its comment in `TiffCcittCompressor.Initialize`.

## Tests

| Test | File | Pre-fix result on 2.1 |
|---|---|---|
| `ReadClut_WithOversizedDimensions_ThrowsInvalidIccProfileException` | `IccDataReaderLutTests.cs` | fails |
| `ReadClut_WithZeroGridPoints_ThrowsInvalidIccProfileException` | `IccDataReaderLutTests.cs` | fails |
| `ReadProfile_WithUndersizedArrayTags_IgnoresTags` | `IccReaderTests.cs` | passes (hardening) |
| `TiffDecoder_DirectoryEntryCountExceedsAvailableData_Throws` | `BigTiffDecoderTests.cs` | fails after an 11 s loop |
| `GlobalHistogramEqualization_ClampsDecodedFloatingPointSamples` (2, -1, +inf, -inf, NaN) | `HistogramEqualizationTests.cs` | test host crashes (AccessViolation) |
| `TiffEncoder_EncodeNarrowAlternatingCcitt_Works` (G3, G4, 1D x width 1, 64) | `TiffEncoderTests.cs` | width 1 G3 and 1D fail |
| `TiffEncoder_ReencodeNarrowCcittGroup4Fax_Works` | `TiffEncoderTests.cs` | fails |

## Deliberate deviations from upstream

- No new code comments (team rule): upstream's explanatory comments are replaced with named
  constants (`BigTiffEntryLength`, `EndOfLineBits`, `MaxBitsPerPixel`, ...). XML docs on the
  new protected abstract member are kept.
- `Numerics.Clamp` instead of `Math.Clamp` (2.1 targets netstandard2.0 and net472).
- `InvalidMemoryOperationException.ThrowAllocationOverLimitException` does not exist in 2.1; the
  exception is constructed inline with the same message shape.
- Collection expressions and range indexers in upstream tests are rewritten for 2.1's language
  version and frameworks (array initializers, `Span.Slice`).
- Extra test cases beyond upstream: NaN/negative/infinite histogram samples, a zero-grid-point
  CLUT, and Group 4 / Modified Huffman narrow-image round trips.

## Verification plan

- Library with no runnable host: the xUnit suite on net6.0 (`SIXLABORS_TESTING_PREVIEW=true`,
  run on the installed .NET 8 runtime with `DOTNET_ROLL_FORWARD=Major`).
- Compile `src/ImageSharp` for every target framework.
- Full suite on the fixed branch and on unmodified `release/2.1.x`; the failure sets must match.
  Known local environment failures: Magick.NET native library (no osx-arm64 build in this
  version), GDI+, and `RemoteExecutor` tests.
- Advisory PoCs: craft the reported inputs and confirm the fixed build rejects them with a
  managed exception, and that GHSA-jj3q / GHSA-v76p inputs raise a managed exception (no process
  crash) on 2.1.

## Residuals

- Packaging: no NuGet package is produced from this fork yet; versioning (MinVer from tags)
  and feed are open.
- `.gitattributes`: git-lfs on a case-insensitive filesystem rewrites the file and drops the
  `*.BMP` line; do not commit that change.
- GHSA-jj3q / GHSA-v76p on 2.1 raise `IndexOutOfRangeException` rather than
  `InvalidImageContentException` for malformed fax strips. Not memory-unsafe; left as is.
