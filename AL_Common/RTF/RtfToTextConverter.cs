﻿/*
Perf log:

             FMInfoGen | RTF_ToPlainTextTest
2024-12-16   ?           2574MB/s (x86) / 3581MB/s (x64)
2024-04-28   ?           2112MB/s (x86) / 2422MB/s (x64)
2024-04-27   ?           2034MB/s (x86) / 2139MB/s (x64)
2024-04-26   ?           1915MB/s (x86) / 2085MB/s (x64)
2023-11-07   ?           1631MB/s (x86) / 2009MB/s (x64)
2023-11-06   ?           1539MB/s (x86) / 1872MB/s (x64)
2023-11-06   ?           1471MB/s (x86) / 1716MB/s (x64)
2023-11-06   ?           1248MB/s (x86) / 1615MB/s (x64)
2023-11-05   ?           1185MB/s (x86) / 1525MB/s (x64)
2023-11-04   ?           1109MB/s (x86) / 1360MB/s (x64)
2023-11-04   ?           906MB/s (x86) / 1101MB/s (x64)
2023-11-03   ?           828MB/s (x86) / 1012MB/s (x64)
2023-11-02   ?           643MB/s (x86) / 713MB/s (x64)
2023-11-02   ?           567MB/s (x86) / 663MB/s (x64)
2023-10-05:  ?           521MB/s (x64)
2023-10-03:  ?           512MB/s (x64)
2023-10-03:  ?           492MB/s (x86)
2023-09-30:  ?           363MB/s (x86)
2023-09-29:  ?           335MB/s (x86)
2020-08-24:  179MB/s     254MB/s
2020-08-20:  157MB/s     211MB/s
2020-08-19:  88.2 MB/s   97MB/s

---

Note to self:
RTFs in 1098 set, base dirs only:
count: 280
total size: 88,714,908
84.6 MB

---

This is a fast RTF-to-plaintext converter designed to provide scannable text for FMScanner.

Goals:
1. Be platform-agnostic (no RichTextBox dependency).
2. Achieve faster performance than RichTextBox, and ideally be as fast as possible beyond that.
3. Accurately convert all "general text" characters as the user would be intended to see them, even symbol font
   glyphs.

Non-Goals:
1. Perfectly match RichTextBox in output. The output is not meant to be displayed to the user, so we don't care
   about extra whitespace lines, indenting, bulleted/numbered list characters, etc. We also convert certain
   exotic characters to their ASCII equivalents (dashes, spaces etc.) to give easier data to the scanner.
2. Extremely strict validation or enforcement of spec. We assume the stream has been checked for an rtf header
   already. We also allow for what RichTextBox allows - or has allowed - for, even if it's not quite to spec.
3. Forward-only stream support. We used to have this, but it entailed a fairly sizable performance loss. The
   need for an un-get buffer caused constant branching inside a tight loop, and also precluded optimizations
   afforded by being able to move the stream index freely.

---

Notes and miscellaneous:
-Hex that combines into an actual valid character: \'81\'63
 (it's supposed to be an ellipsis - __MSG_final__FMInfo-De - Copy.rtf has an instance of it)
-Tiger face (>2 byte Unicode test): \u-9169?\u-10179?

@RTF(Every-char checks):
-RichTextBox respects \v0 (hidden text) when it converts, but LibreOffice doesn't.
-RichTextBox and LibreOffice both remove nulls.

@RTF(RTF to plaintext converter):
-Consider being extremely forgiving about errors - we want as much plaintext as we can get out of a file, and
 even imperfect text may be useful. FMScanner extracts a relatively very small portion of text from the file,
 so statistically it's likely it may not even hit broken text even if it exists.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using JetBrains.Annotations;
using static AL_Common.RTFParserCommon;

namespace AL_Common;

public sealed partial class RtfToTextConverter
{
    public RtfToTextConverter() => InitSymbolFontData();

    #region Constants

    private const int _windows1252 = 1252;
    private const int _shiftJisWin = 932;
    private const char _unicodeUnknown_Char = '\u25A1';

    // Just for robustness, set it to something stupidly high but still small in terms of memory
    private const int _maxSymbolFontNameLength = 32768;

    // 20 bytes * 4 for up to 4 bytes per char. Chars are 2 bytes but like whatever, why do math when you can
    // over-provision.
    private readonly ListFast<char> _charGeneralBuffer = new(20 * 4);

    #endregion

    #region Tables

    #region Font to Unicode conversion tables

    /*
    Many RTF files put emoji-like glyphs into text not with a Unicode character, but by just putting in a
    regular-ass single-byte char and then setting the font to Wingdings or whatever. So the letter "J"
    would show as "☺" in the Wingdings font. If we want to support this lunacy, we need conversion tables
    from known fonts to their closest Unicode mappings. So here we have them.

    These arrays MUST be of length 224, with entries starting at the codepoint for 0x20 and ending at the
    codepoint for 0xFF. That way, they can be arrays instead of dictionaries, making us smaller and faster.
    */

    private const int _symbolArraysStartingIndex = 2;
    private const int _symbolArraysLength = 9;

    private readonly uint[][] _symbolFontTables = new uint[_symbolArraysLength][];
    private readonly byte[][] _symbolFontCharsArrays = new byte[_symbolArraysLength][];

    private void InitSymbolFontData()
    {
        // ReSharper disable RedundantExplicitArraySize
        _symbolFontTables[(int)SymbolFont.Symbol] = new uint[224]
        {
            ' ',
            0x0021,
            0x2200,
            0x0023,
            0x2203,
            0x0025,
            0x0026,
            0x220D,
            0x0028,
            0x0029,
            0x2217,
            0x002B,
            0x002C,
            0x2212,
            0x002E,
            0x002F,
            0x0030,
            0x0031,
            0x0032,
            0x0033,
            0x0034,
            0x0035,
            0x0036,
            0x0037,
            0x0038,
            0x0039,
            0x003A,
            0x003B,
            0x003C,
            0x003D,
            0x003E,
            0x003F,
            0x2245,
            0x0391,
            0x0392,
            0x03A7,
            0x0394,
            0x0395,
            0x03A6,
            0x0393,
            0x0397,
            0x0399,
            0x03D1,
            0x039A,
            0x039B,
            0x039C,
            0x039D,
            0x039F,
            0x03A0,
            0x0398,
            0x03A1,
            0x03A3,
            0x03A4,
            0x03A5,
            0x03C2,
            0x03A9,
            0x039E,
            0x03A8,
            0x0396,
            0x005B,
            0x2234,
            0x005D,
            0x22A5,
            0x005F,

            // Supposed to be " ‾" but closest Unicode char is "‾" (0x203E)
            0x203E,

            0x03B1,
            0x03B2,
            0x03C7,
            0x03B4,
            0x03B5,

            // Nominally lowercase phi (0x3C6), but is uppercase phi in Windows Symbol
            0x03C6,

            0x03B3,
            0x03B7,
            0x03B9,

            // Nominally uppercase phi (0x3D5), but is lowercase phi in Windows Symbol
            0x03D5,

            0x03BA,
            0x03BB,
            0x03BC,
            0x03BD,
            0x03BF,
            0x03C0,
            0x03B8,
            0x03C1,
            0x03C3,
            0x03C4,
            0x03C5,
            0x03D6,
            0x03C9,
            0x03BE,
            0x03C8,
            0x03B6,
            0x007B,
            0x007C,
            0x007D,
            0x223C,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,

            // Euro sign, but undefined in Win10 Symbol font at least
            0x20AC,

            0x03D2,
            0x2032,
            0x2264,
            0x2044,
            0x221E,
            0x0192,
            0x2663,
            0x2666,
            0x2665,
            0x2660,
            0x2194,
            0x2190,
            0x2191,
            0x2192,
            0x2193,
            0x00B0,
            0x00B1,
            0x2033,
            0x2265,
            0x00D7,
            0x221D,
            0x2202,
            0x2022,
            0x00F7,
            0x2260,
            0x2261,
            0x2248,
            0x2026,
            0x23D0,
            0x23AF,
            0x21B5,
            0x2135,
            0x2111,
            0x211C,
            0x2118,
            0x2297,
            0x2295,
            0x2205,
            0x2229,
            0x222A,
            0x2283,
            0x2287,
            0x2284,
            0x2282,
            0x2286,
            0x2208,
            0x2209,
            0x2220,
            0x2207,

            // First set of (R), (TM), (C) (nominally serif)
            0x00AE,
            0x00A9,
            0x2122,

            0x220F,
            0x221A,
            0x22C5,
            0x00AC,
            0x2227,
            0x2228,
            0x21D4,
            0x21D0,
            0x21D1,
            0x21D2,
            0x21D3,
            0x25CA,
            0x2329,

            // Second set of (R), (TM), (C) (nominally sans-serif)
            0x00AE,
            0x00A9,
            0x2122,

            0x2211,
            0x239B,
            0x239C,
            0x239D,
            0x23A1,
            0x23A2,
            0x23A3,
            0x23A7,
            0x23A8,
            0x23A9,
            0x23AA,

            // Apple logo. Using "RED APPLE".
            0x1F34E,

            0x232A,
            0x222B,
            0x2320,
            0x23AE,
            0x2321,
            0x239E,
            0x239F,
            0x23A0,
            0x23A4,
            0x23A5,
            0x23A6,
            0x23AB,
            0x23AC,
            0x23AD,
            _unicodeUnknown_Char,
        };

        _symbolFontTables[(int)SymbolFont.Wingdings] = new uint[224]
        {
            ' ',
            0x1F589,
            0x2702,
            0x2701,
            0x1F453,
            0x1F56D,
            0x1F56E,
            0x1F56F,
            0x1F57F,
            0x2706,
            0x1F582,
            0x1F583,
            0x1F4EA,
            0x1F4EB,
            0x1F4EC,
            0x1F4ED,
            0x1F5C0,
            0x1F5C1,
            0x1F5CE,
            0x1F5CF,
            0x1F5D0,
            0x1F5C4,
            0x231B,
            0x1F5AE,
            0x1F5B0,
            0x1F5B2,
            0x1F5B3,
            0x1F5B4,
            0x1F5AB,
            0x1F5AC,
            0x2707,
            0x270D,
            0x1F58E,
            0x270C,
            0x1F58F,
            0x1F44D,
            0x1F44E,
            0x261C,
            0x261E,
            0x261D,
            0x261F,
            0x1F590,
            0x263A,
            0x1F610,
            0x2639,
            0x1F4A3,
            0x1F571,
            0x1F3F3,
            0x1F3F1,
            0x2708,
            0x263C,
            0x1F322,
            0x2744,
            0x1F546,
            0x271E,
            0x1F548,
            0x2720,
            0x2721,
            0x262A,
            0x262F,
            0x1F549,
            0x2638,
            0x2648,
            0x2649,
            0x264A,
            0x264B,
            0x264C,
            0x264D,
            0x264E,
            0x264F,
            0x2650,
            0x2651,
            0x2652,
            0x2653,
            0x1F670,
            0x1F675,
            0x26AB,
            0x1F53E,
            0x25FC,
            0x1F78F,
            0x1F790,
            0x2751,
            0x2752,
            0x1F79F,
            0x29EB,
            0x25C6,
            0x2756,
            0x2B29,
            0x2327,
            0x2BB9,
            0x2318,
            0x1F3F5,
            0x1F3F6,
            0x1F676,
            0x1F677,
            _unicodeUnknown_Char,
            0x1F10B,
            0x2780,
            0x2781,
            0x2782,
            0x2783,
            0x2784,
            0x2785,
            0x2786,
            0x2787,
            0x2788,
            0x2789,
            0x1F10C,
            0x278A,
            0x278B,
            0x278C,
            0x278D,
            0x278E,
            0x278F,
            0x2790,
            0x2791,
            0x2792,
            0x2793,
            0x1F662,
            0x1F660,
            0x1F661,
            0x1F663,
            0x1F65E,
            0x1F65C,
            0x1F65D,
            0x1F65F,
            0x2219,
            0x2022,
            0x2B1D,
            0x2B58,
            0x1F786,
            0x1F788,
            0x1F78A,
            0x1F78B,
            0x1F53F,
            0x25AA,
            0x1F78E,
            0x1F7C1,
            0x1F7C5,
            0x2605,
            0x1F7CB,
            0x1F7CF,
            0x1F7D3,
            0x1F7D1,
            0x2BD0,
            0x2316,
            0x2BCE,
            0x2BCF,
            0x2BD1,
            0x272A,
            0x2730,
            0x1F550,
            0x1F551,
            0x1F552,
            0x1F553,
            0x1F554,
            0x1F555,
            0x1F556,
            0x1F557,
            0x1F558,
            0x1F559,
            0x1F55A,
            0x1F55B,
            0x2BB0,
            0x2BB1,
            0x2BB2,
            0x2BB3,
            0x2BB4,
            0x2BB5,
            0x2BB6,
            0x2BB7,
            0x1F66A,
            0x1F66B,
            0x1F655,
            0x1F654,
            0x1F657,
            0x1F656,
            0x1F650,
            0x1F651,
            0x1F652,
            0x1F653,
            0x232B,
            0x2326,
            0x2B98,
            0x2B9A,
            0x2B99,
            0x2B9B,
            0x2B88,
            0x2B8A,
            0x2B89,
            0x2B8B,
            0x1F868,
            0x1F86A,
            0x1F869,
            0x1F86B,
            0x1F86C,
            0x1F86D,
            0x1F86F,
            0x1F86E,
            0x1F878,
            0x1F87A,
            0x1F879,
            0x1F87B,
            0x1F87C,
            0x1F87D,
            0x1F87F,
            0x1F87E,
            0x21E6,
            0x21E8,
            0x21E7,
            0x21E9,
            0x2B04,
            0x21F3,
            0x2B01,
            0x2B00,
            0x2B03,
            0x2B02,
            0x1F8AC,
            0x1F8AD,
            0x1F5F6,
            0x2713,
            0x1F5F7,
            0x1F5F9,

            // Windows logo. Using "WINDOW".
            0x1FA9F,
        };

        _symbolFontTables[(int)SymbolFont.Wingdings2] = new uint[224]
        {
            ' ',
            0x1F58A,
            0x1F58B,
            0x1F58C,
            0x1F58D,
            0x2704,
            0x2700,
            0x1F57E,
            0x1F57D,
            0x1F5C5,
            0x1F5C6,
            0x1F5C7,
            0x1F5C8,
            0x1F5C9,
            0x1F5CA,
            0x1F5CB,
            0x1F5CC,
            0x1F5CD,
            0x1F4CB,
            0x1F5D1,
            0x1F5D4,
            0x1F5B5,
            0x1F5B6,
            0x1F5B7,
            0x1F5B8,
            0x1F5AD,
            0x1F5AF,
            0x1F5B1,
            0x1F592,
            0x1F593,
            0x1F598,
            0x1F599,
            0x1F59A,
            0x1F59B,
            0x1F448,
            0x1F449,
            0x1F59C,
            0x1F59D,
            0x1F59E,
            0x1F59F,
            0x1F5A0,
            0x1F5A1,
            0x1F446,
            0x1F447,
            0x1F5A2,
            0x1F5A3,
            0x1F591,
            0x1F5F4,
            0x1F5F8,
            0x1F5F5,
            0x2611,
            0x2BBD,
            0x2612,
            0x2BBE,
            0x2BBF,
            0x1F6C7,
            0x29B8,
            0x1F671,
            0x1F674,
            0x1F672,
            0x1F673,
            0x203D,
            0x1F679,
            0x1F67A,
            0x1F67B,
            0x1F666,
            0x1F664,
            0x1F665,
            0x1F667,
            0x1F65A,
            0x1F658,
            0x1F659,
            0x1F65B,
            0x24EA,
            0x2460,
            0x2461,
            0x2462,
            0x2463,
            0x2464,
            0x2465,
            0x2466,
            0x2467,
            0x2468,
            0x2469,
            0x24FF,
            0x2776,
            0x2777,
            0x2778,
            0x2779,
            0x277A,
            0x277B,
            0x277C,
            0x277D,
            0x277E,
            0x277F,
            _unicodeUnknown_Char,
            0x2609,
            0x1F315,
            0x263D,
            0x263E,
            0x2E3F,
            0x271D,
            0x1F547,
            0x1F55C,
            0x1F55D,
            0x1F55E,
            0x1F55F,
            0x1F560,
            0x1F561,
            0x1F562,
            0x1F563,
            0x1F564,
            0x1F565,
            0x1F566,
            0x1F567,
            0x1F668,
            0x1F669,
            0x22C5,
            0x1F784,
            0x2981,
            0x25CF,
            0x25CB,
            0x1F785,
            0x1F787,
            0x1F789,
            0x2299,
            0x29BF,
            0x1F78C,
            0x1F78D,
            0x25FE,
            0x25A0,
            0x25A1,
            0x1F791,
            0x1F792,
            0x1F793,
            0x1F794,
            0x25A3,
            0x1F795,
            0x1F796,
            0x1F797,
            0x1F798,
            0x2B29,
            0x2B25,
            0x25C7,
            0x1F79A,
            0x25C8,
            0x1F79B,
            0x1F79C,
            0x1F79D,
            0x1F79E,
            0x2B2A,
            0x2B27,
            0x25CA,
            0x1F7A0,
            0x25D6,
            0x25D7,
            0x2BCA,
            0x2BCB,
            0x2BC0,
            0x2BC1,
            0x2B1F,
            0x2BC2,
            0x2B23,
            0x2B22,
            0x2BC3,
            0x2BC4,
            0x1F7A1,
            0x1F7A2,
            0x1F7A3,
            0x1F7A4,
            0x1F7A5,
            0x1F7A6,
            0x1F7A7,
            0x1F7A8,
            0x1F7A9,
            0x1F7AA,
            0x1F7AB,
            0x1F7AC,
            0x1F7AD,
            0x1F7AE,
            0x1F7AF,
            0x1F7B0,
            0x1F7B1,
            0x1F7B2,
            0x1F7B3,
            0x1F7B4,
            0x1F7B5,
            0x1F7B6,
            0x1F7B7,
            0x1F7B8,
            0x1F7B9,
            0x1F7BA,
            0x1F7BB,
            0x1F7BC,
            0x1F7BD,
            0x1F7BE,
            0x1F7BF,
            0x1F7C0,
            0x1F7C2,
            0x1F7C4,
            0x1F7C6,
            0x1F7C9,
            0x1F7CA,
            0x2736,
            0x1F7CC,
            0x1F7CE,
            0x1F7D0,
            0x1F7D2,
            0x2739,
            0x1F7C3,
            0x1F7C7,
            0x272F,
            0x1F7CD,
            0x1F7D4,
            0x2BCC,
            0x2BCD,
            0x203B,
            0x2042,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
        };

        _symbolFontTables[(int)SymbolFont.Wingdings3] = new uint[224]
        {
            ' ',
            0x2B60,
            0x2B62,
            0x2B61,
            0x2B63,
            0x2B66,
            0x2B67,
            0x2B69,
            0x2B68,
            0x2B70,
            0x2B72,
            0x2B71,
            0x2B73,
            0x2B76,
            0x2B78,
            0x2B7B,
            0x2B7D,
            0x2B64,
            0x2B65,
            0x2B6A,
            0x2B6C,
            0x2B6B,
            0x2B6D,
            0x2B4D,
            0x2BA0,
            0x2BA1,
            0x2BA2,
            0x2BA3,
            0x2BA4,
            0x2BA5,
            0x2BA6,
            0x2BA7,
            0x2B90,
            0x2B91,
            0x2B92,
            0x2B93,
            0x2B80,
            0x2B83,
            0x2B7E,
            0x2B7F,
            0x2B84,
            0x2B86,
            0x2B85,
            0x2B87,
            0x2B8F,
            0x2B8D,
            0x2B8E,
            0x2B8C,
            0x2B6E,
            0x2B6F,
            0x238B,
            0x2324,
            0x2303,
            0x2325,
            0x2423,
            0x237D,
            0x21EA,
            0x2BB8,
            0x1F8A0,
            0x1F8A1,
            0x1F8A2,
            0x1F8A3,
            0x1F8A4,
            0x1F8A5,
            0x1F8A6,
            0x1F8A7,
            0x1F8A8,
            0x1F8A9,
            0x1F8AA,
            0x1F8AB,
            0x1F850,
            0x1F852,
            0x1F851,
            0x1F853,
            0x1F854,
            0x1F855,
            0x1F857,
            0x1F856,
            0x1F858,
            0x1F859,
            0x25B2,
            0x25BC,
            0x25B3,
            0x25BD,
            0x25C0,
            0x25B6,
            0x25C1,
            0x25B7,
            0x25E3,
            0x25E2,
            0x25E4,
            0x25E5,
            0x1F780,
            0x1F782,
            0x1F781,
            _unicodeUnknown_Char,
            0x1F783,
            0x2BC5,
            0x2BC6,
            0x2BC7,
            0x2BC8,
            0x2B9C,
            0x2B9E,
            0x2B9D,
            0x2B9F,
            0x1F810,
            0x1F812,
            0x1F811,
            0x1F813,
            0x1F814,
            0x1F816,
            0x1F815,
            0x1F817,
            0x1F818,
            0x1F81A,
            0x1F819,
            0x1F81B,
            0x1F81C,
            0x1F81E,
            0x1F81D,
            0x1F81F,
            0x1F800,
            0x1F802,
            0x1F801,
            0x1F803,
            0x1F804,
            0x1F806,
            0x1F805,
            0x1F807,
            0x1F808,
            0x1F80A,
            0x1F809,
            0x1F80B,
            0x1F820,
            0x1F822,
            0x1F824,
            0x1F826,
            0x1F828,
            0x1F82A,
            0x1F82C,
            0x1F89C,
            0x1F89D,
            0x1F89E,
            0x1F89F,
            0x1F82E,
            0x1F830,
            0x1F832,
            0x1F834,
            0x1F836,
            0x1F838,
            0x1F83A,
            0x1F839,
            0x1F83B,
            0x1F898,
            0x1F89A,
            0x1F899,
            0x1F89B,
            0x1F83C,
            0x1F83E,
            0x1F83D,
            0x1F83F,
            0x1F840,
            0x1F842,
            0x1F841,
            0x1F843,
            0x1F844,
            0x1F846,
            0x1F845,
            0x1F847,
            0x2BA8,
            0x2BA9,
            0x2BAA,
            0x2BAB,
            0x2BAC,
            0x2BAD,
            0x2BAE,
            0x2BAF,
            0x1F860,
            0x1F862,
            0x1F861,
            0x1F863,
            0x1F864,
            0x1F865,
            0x1F867,
            0x1F866,
            0x1F870,
            0x1F872,
            0x1F871,
            0x1F873,
            0x1F874,
            0x1F875,
            0x1F877,
            0x1F876,
            0x1F880,
            0x1F882,
            0x1F881,
            0x1F883,
            0x1F884,
            0x1F885,
            0x1F887,
            0x1F886,
            0x1F890,
            0x1F892,
            0x1F891,
            0x1F893,
            0x1F894,
            0x1F896,
            0x1F895,
            0x1F897,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
        };

        _symbolFontTables[(int)SymbolFont.Webdings] = new uint[224]
        {
            ' ',
            0x1F577,
            0x1F578,
            0x1F572,
            0x1F576,
            0x1F3C6,
            0x1F396,
            0x1F587,
            0x1F5E8,
            0x1F5E9,
            0x1F5F0,
            0x1F5F1,
            0x1F336,
            0x1F397,
            0x1F67E,
            0x1F67C,
            0x1F5D5,
            0x1F5D6,
            0x1F5D7,
            0x23F4,
            0x23F5,
            0x23F6,
            0x23F7,
            0x23EA,
            0x23E9,
            0x23EE,
            0x23ED,
            0x23F8,
            0x23F9,
            0x23FA,
            0x1F5DA,
            0x1F5F3,
            0x1F6E0,
            0x1F3D7,
            0x1F3D8,
            0x1F3D9,
            0x1F3DA,
            0x1F3DC,
            0x1F3ED,
            0x1F3DB,
            0x1F3E0,
            0x1F3D6,
            0x1F3DD,
            0x1F6E3,
            0x1F50D,
            0x1F3D4,
            0x1F441,
            0x1F442,
            0x1F3DE,
            0x1F3D5,
            0x1F6E4,
            0x1F3DF,
            0x1F6F3,
            0x1F56C,
            0x1F56B,
            0x1F568,
            0x1F508,
            0x1F394,
            0x1F395,
            0x1F5EC,
            0x1F67D,
            0x1F5ED,
            0x1F5EA,
            0x1F5EB,
            0x2B94,
            0x2714,
            0x1F6B2,
            0x2B1C,
            0x1F6E1,
            0x1F381,
            0x1F6F1,
            0x2B1B,
            0x1F691,
            0x1F6C8,
            0x1F6E9,
            0x1F6F0,
            0x1F7C8,
            0x1F574,
            0x2B24,
            0x1F6E5,
            0x1F694,
            0x1F5D8,
            0x1F5D9,
            0x2753,
            0x1F6F2,
            0x1F687,
            0x1F68D,
            0x1F6A9,
            0x29B8,
            0x2296,
            0x1F6AD,
            0x1F5EE,
            0x23D0,
            0x1F5EF,
            0x1F5F2,

            _unicodeUnknown_Char,

            0x1F6B9,
            0x1F6BA,
            0x1F6C9,
            0x1F6CA,
            0x1F6BC,
            0x1F47D,
            0x1F3CB,
            0x26F7,
            0x1F3C2,
            0x1F3CC,
            0x1F3CA,
            0x1F3C4,
            0x1F3CD,
            0x1F3CE,
            0x1F698,
            0x1F4C8,
            0x1F6E2,
            0x1F4B0,
            0x1F3F7,
            0x1F4B3,
            0x1F46A,
            0x1F5E1,
            0x1F5E2,
            0x1F5E3,
            0x272F,
            0x1F584,
            0x1F585,
            0x1F583,
            0x1F586,
            0x1F5B9,
            0x1F5BA,
            0x1F5BB,
            0x1F575,
            0x1F570,
            0x1F5BD,
            0x1F5BE,
            0x1F4CB,
            0x1F5D2,
            0x1F5D3,
            0x1F56E,
            0x1F4DA,
            0x1F5DE,
            0x1F5DF,
            0x1F5C3,
            0x1F4C7,
            0x1F5BC,
            0x1F3AD,
            0x1F39C,
            0x1F398,
            0x1F399,
            0x1F3A7,
            0x1F4BF,
            0x1F39E,
            0x1F4F7,
            0x1F39F,
            0x1F3AC,
            0x1F4FD,
            0x1F4F9,
            0x1F4FE,
            0x1F4FB,
            0x1F39A,
            0x1F39B,
            0x1F4FA,
            0x1F4BB,
            0x1F5A5,
            0x1F5A6,
            0x1F5A7,
            0x1F579,
            0x1F3AE,
            0x1F57B,
            0x1F57C,
            0x1F4DF,
            0x1F581,
            0x1F580,
            0x1F5A8,
            0x1F5A9,
            0x1F5BF,
            0x1F5AA,
            0x1F5DC,
            0x1F512,
            0x1F513,
            0x1F5DD,
            0x1F4E5,
            0x1F4E4,
            0x1F573,
            0x1F323,
            0x1F324,
            0x1F325,
            0x1F326,
            0x2601,
            0x1F328,
            0x1F327,
            0x1F329,
            0x1F32A,
            0x1F32C,
            0x1F32B,
            0x1F31C,
            0x1F321,
            0x1F6CB,
            0x1F6CF,
            0x1F37D,
            0x1F378,
            0x1F6CE,
            0x1F6CD,
            0x24C5,
            0x267F,
            0x1F6C6,
            0x1F588,
            0x1F393,
            0x1F5E4,
            0x1F5E5,
            0x1F5E6,
            0x1F5E7,
            0x1F6EA,
            0x1F43F,
            0x1F426,
            0x1F41F,
            0x1F415,
            0x1F408,
            0x1F66C,
            0x1F66E,
            0x1F66D,
            0x1F66F,
            0x1F5FA,
            0x1F30D,
            0x1F30F,
            0x1F30E,
            0x1F54A,
        };

        _symbolFontTables[(int)SymbolFont.ITCZapfDingbats] = new uint[224]
        {
            ' ',
            0x2701,
            0x2702,
            0x2703,
            0x2704,
            0x260E,
            0x2706,
            0x2707,
            0x2708,
            0x2709,
            0x261B,
            0x261E,
            0x270C,
            0x270D,
            0x270E,
            0x270F,
            0x2710,
            0x2711,
            0x2712,
            0x2713,
            0x2714,
            0x2715,
            0x2716,
            0x2717,
            0x2718,
            0x2719,
            0x271A,
            0x271B,
            0x271C,
            0x271D,
            0x271E,
            0x271F,
            0x2720,
            0x2721,
            0x2722,
            0x2723,
            0x2724,
            0x2725,
            0x2726,
            0x2727,
            0x2605,
            0x2729,
            0x272A,
            0x272B,
            0x272C,
            0x272D,
            0x272E,
            0x272F,
            0x2730,
            0x2731,
            0x2732,
            0x2733,
            0x2734,
            0x2735,
            0x2736,
            0x2737,
            0x2738,
            0x2739,
            0x273A,
            0x273B,
            0x273C,
            0x273D,
            0x273E,
            0x273F,
            0x2740,
            0x2741,
            0x2742,
            0x2743,
            0x2744,
            0x2745,
            0x2746,
            0x2747,
            0x2748,
            0x2749,
            0x274A,
            0x274B,
            0x25CF,
            0x274D,
            0x25A0,
            0x274F,
            0x2750,
            0x2751,
            0x2752,
            0x25B2,
            0x25BC,
            0x25C6,
            0x2756,
            0x25D7,
            0x2758,
            0x2759,
            0x275A,
            0x275B,
            0x275C,
            0x275D,
            0x275E,
            _unicodeUnknown_Char,
            0x2768,
            0x2769,
            0x276A,
            0x276B,
            0x276C,
            0x276D,
            0x276E,
            0x276F,
            0x2770,
            0x2771,
            0x2772,
            0x2773,
            0x2774,
            0x2775,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            _unicodeUnknown_Char,
            0x2761,
            0x2762,
            0x2763,
            0x2764,
            0x2765,
            0x2766,
            0x2767,
            0x2663,
            0x2666,
            0x2665,
            0x2660,
            0x2460,
            0x2461,
            0x2462,
            0x2463,
            0x2464,
            0x2465,
            0x2466,
            0x2467,
            0x2468,
            0x2469,
            0x2776,
            0x2777,
            0x2778,
            0x2779,
            0x277A,
            0x277B,
            0x277C,
            0x277D,
            0x277E,
            0x277F,
            0x2780,
            0x2781,
            0x2782,
            0x2783,
            0x2784,
            0x2785,
            0x2786,
            0x2787,
            0x2788,
            0x2789,
            0x278A,
            0x278B,
            0x278C,
            0x278D,
            0x278E,
            0x278F,
            0x2790,
            0x2791,
            0x2792,
            0x2793,
            0x2794,
            0x2192,
            0x2194,
            0x2195,
            0x2798,
            0x2799,
            0x279A,
            0x279B,
            0x279C,
            0x279D,
            0x279E,
            0x279F,
            0x27A0,
            0x27A1,
            0x27A2,
            0x27A3,
            0x27A4,
            0x27A5,
            0x27A6,
            0x27A7,
            0x27A8,
            0x27A9,
            0x27AA,
            0x27AB,
            0x27AC,
            0x27AD,
            0x27AE,
            0x27AF,
            _unicodeUnknown_Char,
            0x27B1,
            0x27B2,
            0x27B3,
            0x27B4,
            0x27B5,
            0x27B6,
            0x27B7,
            0x27B8,
            0x27B9,
            0x27BA,
            0x27BB,
            0x27BC,
            0x27BD,
            0x27BE,
            _unicodeUnknown_Char,
        };

        _symbolFontTables[(int)SymbolFont.ZapfDingbats] = _symbolFontTables[(int)SymbolFont.ITCZapfDingbats];
        // ReSharper restore RedundantExplicitArraySize

        _symbolFontCharsArrays[(int)SymbolFont.Symbol] = "Symbol"u8.ToArray();
        _symbolFontCharsArrays[(int)SymbolFont.Wingdings] = "Wingdings"u8.ToArray();
        _symbolFontCharsArrays[(int)SymbolFont.Wingdings2] = "Wingdings 2"u8.ToArray();
        _symbolFontCharsArrays[(int)SymbolFont.Wingdings3] = "Wingdings 3"u8.ToArray();
        _symbolFontCharsArrays[(int)SymbolFont.Webdings] = "Webdings"u8.ToArray();
        _symbolFontCharsArrays[(int)SymbolFont.ITCZapfDingbats] = "ITC Zapf Dingbats"u8.ToArray();
        _symbolFontCharsArrays[(int)SymbolFont.ZapfDingbats] = "Zapf Dingbats"u8.ToArray();
    }

    #endregion

    // From .NET 8. Maps ascii characters to hex (ie. "A" -> 0xA). 0xFF means not a hex character.
    private static readonly byte[] _charToHex =
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0x0, 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8, 0x9, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xA, 0xB, 0xC, 0xD, 0xE, 0xF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xa, 0xb, 0xc, 0xd, 0xe, 0xf, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    };

    private readonly bool[] IsNonPlainText = RunFunc(static () =>
    {
        bool[] ret = new bool[256];
        ret['\\'] = true;
        ret['{'] = true;
        ret['}'] = true;
        ret['\r'] = true;
        ret['\n'] = true;
        ret['\0'] = true;
        return ret;
    });

    #endregion

    #region Resettables

    /*
    Per spec, if we see a \uN keyword whose N falls within the range of 0xF020 to 0xF0FF, we're supposed to
    subtract 0xF000 and then find the last used font whose charset is 2 (codepage 42) and use its symbol font
    to convert the char. However, when the spec says "last used" it REALLY means last used. Period. Regardless
    of group. Even if the font was used in a group above us that we should have no knowledge of, it still
    counts as the last used one. Also, we need the last used font WHOSE CODEPAGE IS 42, not the last used font
    period. So we have to track only the charset 2/codepage 42 ones. Globally. Truly bizarre.
    */
    private int _lastUsedFontWithCodePage42 = NoFontNumber;

    // Highest measured was 56192
    private readonly ListFast<char> _plainText = new(ByteSize.KB * 60);

    private const int _fldinstSymbolNumberMaxLen = 10;
    private readonly ListFast<char> _fldinstSymbolNumber = new(_fldinstSymbolNumberMaxLen);

    private readonly ListFast<char> _fldinstSymbolFontName = new(32);

    // Highest measured was 17
    private readonly ListFast<byte> _hexBuffer = new(20);

    // Highest measured was 13
    private readonly ListFast<char> _unicodeBuffer = new(20);

    private readonly ListFast<char> _symbolFontNameBuffer = new(32);

    private bool _inHandleFontTable;

    #endregion

    #region Reusable buffers

    private readonly byte[] _byteBuffer1 = new byte[1];
    private readonly byte[] _byteBuffer4 = new byte[4];

    #endregion

    #region Cached encodings

    // DON'T reset this. We want to build up a dictionary of encodings and amortize it over the entire list
    // of RTF files.
    private readonly Dictionary<int, Encoding> _encodings = new(31);

    // Common ones explicitly stored to avoid even a dictionary lookup. Don't reset these either.
    private readonly Encoding _windows1252Encoding = Encoding.GetEncoding(_windows1252);

    private readonly Encoding _windows1250Encoding = Encoding.GetEncoding(1250);

    private readonly Encoding _windows1251Encoding = Encoding.GetEncoding(1251);

    private readonly Encoding _shiftJisWinEncoding = Encoding.GetEncoding(_shiftJisWin);

    #endregion

    #region Public API

    [PublicAPI]
    public (bool Success, string Text)
    Convert(in ArrayWithLength<byte> rtfBytes)
    {
        Reset(rtfBytes);

#if ReleaseRTFTest || DebugRTFTest
        RtfError error = ParseRtf();
        return error == RtfError.OK ? (true, CreateStringFromChars(_plainText)) : throw new System.Exception("RTF converter error: " + error);
        //return error == Error.OK ? (true, "") : throw new Exception("RTF converter error: " + error);
#else
        try
        {
            RtfError error = ParseRtf();
            return error == RtfError.OK ? (true, CreateStringFromChars(_plainText)) : (false, "");
        }
        catch
        {
            return (false, "");
        }
        finally
        {
            _rtfBytes = ArrayWithLength<byte>.Empty();
        }
#endif
    }

    #endregion

    private void Reset(in ArrayWithLength<byte> rtfBytes)
    {
        ResetBase(rtfBytes);

        #region Fixed-size fields

        // Specific capacity and won't grow; no need to deallocate
        _fldinstSymbolNumber.ClearFast();

        _lastUsedFontWithCodePage42 = NoFontNumber;

        #endregion

        _hexBuffer.ClearFast();
        _unicodeBuffer.ClearFast();
        _plainText.ClearFast();
        _fldinstSymbolFontName.ClearFast();
        _symbolFontNameBuffer.ClearFast();

        // Extremely unlikely we'll hit any of these, but just for safety
        if (_hexBuffer.Capacity > ByteSize.MB) _hexBuffer.Capacity = 0;
        if (_unicodeBuffer.Capacity > ByteSize.MB) _unicodeBuffer.Capacity = 0;
        // For the font entries, we can't check a Dictionary's capacity nor set it, so... oh well.
        // @NET5: I think .NET modern has dictionary clear methods?
        if (_plainText.Capacity > ByteSize.MB) _plainText.Capacity = 0;
        if (_fldinstSymbolFontName.Capacity > ByteSize.MB) _fldinstSymbolFontName.Capacity = 0;
        if (_symbolFontNameBuffer.Capacity > ByteSize.MB) _symbolFontNameBuffer.Capacity = 0;

        _inHandleFontTable = false;
    }

    private RtfError ParseRtf()
    {
        while (CurrentPos < _rtfBytes.Length)
        {
            char ch = (char)_rtfBytes.Array[CurrentPos++];

            // Ordered by most frequently appearing first
            switch (ch)
            {
                case '\\':
                    RtfError ec = ParseKeyword();
                    if (ec != RtfError.OK) return ec;
                    break;
                case '{':
                    _ctx.GroupStack.DeepCopyToNext();
                    _groupCount++;
                    break;
                case '}':
                    if (_ctx.GroupStack.Count == 0) return RtfError.StackUnderflow;
                    --_ctx.GroupStack.Count;
                    _groupCount--;
                    if (_groupCount == 0) return RtfError.OK;
                    break;
                case '\r':
                case '\n':
                    break;
                case not '\0':
                {
                    GroupStack groupStack = _ctx.GroupStack;
                    if (!groupStack.CurrentSkipDest &&
                        groupStack.CurrentProperties[(int)Property.Hidden] == 0)
                    {
                        if (IsNonPlainText[_rtfBytes.Array[CurrentPos]])
                        {
                            SymbolFont symbolFont = groupStack.CurrentSymbolFont;
                            if (symbolFont > SymbolFont.Unset)
                            {
                                GetCharFromConversionList_Byte((byte)ch, _symbolFontTables[(int)symbolFont], out ListFast<char> result);
                                _plainText.AddRange(result);
                            }
                            else
                            {
                                _plainText.Add(ch);
                            }
                        }
                        else
                        {
                            HandlePlainTextRun();
                        }
                    }
                    break;
                }
            }
        }

        return _groupCount > 0 ? RtfError.UnmatchedBrace : RtfError.OK;
    }

    private void HandlePlainTextRun()
    {
        CurrentPos--;

        SymbolFont symbolFont = _ctx.GroupStack.CurrentSymbolFont;
        if (symbolFont > SymbolFont.Unset)
        {
            uint[] table = _symbolFontTables[(int)symbolFont];
            while (CurrentPos < _rtfBytes.Length)
            {
                char ch = (char)_rtfBytes.Array[CurrentPos++];
                if (!IsNonPlainText[ch])
                {
                    GetCharFromConversionList_Byte((byte)ch, table, out ListFast<char> result);
                    _plainText.AddRange(result);
                }
                else
                {
                    CurrentPos--;
                    break;
                }
            }
        }
        else
        {
            while (CurrentPos < _rtfBytes.Length)
            {
                char ch = (char)_rtfBytes.Array[CurrentPos++];
                if (!IsNonPlainText[ch])
                {
                    _plainText.Add(ch);
                }
                else
                {
                    CurrentPos--;
                    break;
                }
            }
        }
    }

    #region Act on keywords

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RtfError DispatchKeyword(Symbol symbol, int param, bool hasParam)
    {
        if (!_ctx.GroupStack.CurrentSkipDest)
        {
            switch (symbol.KeywordType)
            {
                case KeywordType.Property:
                    if (symbol.UseDefaultParam || !hasParam) param = symbol.DefaultParam;
                    ChangeProperty((Property)symbol.Index, param);
                    return RtfError.OK;
                case KeywordType.Character:
                    ParseChar_Explicit((char)symbol.Index);
                    return RtfError.OK;
                case KeywordType.Destination:
                    return symbol.Index == (int)DestinationType.SkippableHex
                        ? HandleSkippableHexData(param)
                        : ChangeDestination((DestinationType)symbol.Index);
                case KeywordType.Special:
                    SpecialType specialType = (SpecialType)symbol.Index;
                    return DispatchSpecialKeyword(specialType, symbol, param);
                default:
                    return RtfError.OK;
            }
        }
        else
        {
            switch (symbol.KeywordType)
            {
                case KeywordType.Destination:
                    return symbol.Index == (int)DestinationType.SkippableHex
                        ? HandleSkippableHexData(param)
                        : RtfError.OK;
                case KeywordType.Special:
                    SpecialType specialType = (SpecialType)symbol.Index;
                    return specialType == SpecialType.SkipNumberOfBytes
                        ? DispatchSpecialKeyword(specialType, symbol, param)
                        : RtfError.OK;
                default:
                    return RtfError.OK;
            }
        }
    }

    private RtfError DispatchSpecialKeyword(SpecialType specialType, Symbol symbol, int param)
    {
        switch (specialType)
        {
            case SpecialType.SkipNumberOfBytes:
                if (symbol.UseDefaultParam) param = symbol.DefaultParam;
                if (param < 0) return RtfError.AbortedForSafety;
                CurrentPos += param;
                break;
            case SpecialType.HexEncodedChar:
                HandleHexRun();
                break;
            case SpecialType.UnicodeChar:
            {
                HandleUnicodeParamAndSkipFallbackChars(param);
                RtfError error = HandleUnicodeRun();
                if (error != RtfError.OK) return error;
                break;
            }
            case SpecialType.ColorTable:
                int closingBraceIndex = Array_IndexOfByte_Fast(_rtfBytes.Array, (byte)'}', CurrentPos, _rtfBytes.Length - CurrentPos);
                CurrentPos = closingBraceIndex == -1 ? _rtfBytes.Length : closingBraceIndex;
                break;
            case SpecialType.FontTable:
            {
                _ctx.GroupStack.CurrentInFontTable = true;
                RtfError error = HandleFontTable();
                if (error != RtfError.OK) return error;
                break;
            }
            default:
                HandleSpecialTypeFont(_ctx, specialType, param);
                break;
        }

        return RtfError.OK;
    }

    private RtfError HandleFontTable()
    {
        // Prevent stack overflow from maliciously-crafted rtf files - we should never recurse back into here in
        // a spec-conforming file.
        if (_inHandleFontTable) return RtfError.AbortedForSafety;
        _inHandleFontTable = true;

        int fontTableGroupLevel = _ctx.GroupStack.Count;

        while (CurrentPos < _rtfBytes.Length)
        {
            char ch = (char)_rtfBytes.Array[CurrentPos++];

            switch (ch)
            {
                case '{':
                    _ctx.GroupStack.DeepCopyToNext();
                    _groupCount++;
                    break;
                case '}':
                    if (_ctx.GroupStack.Count == 0) return RtfError.StackUnderflow;
                    --_ctx.GroupStack.Count;
                    _groupCount--;
                    if (_groupCount < fontTableGroupLevel)
                    {
                        // We can't actually set the symbol font as soon as we see \deffN, because we won't have
                        // any font entry objects yet. Now that we do, we can retroactively set all previous
                        // groups' fonts as appropriate, as if they had propagated up automatically.
                        int defaultFontNum = _ctx.Header.DefaultFontNum;
                        if (_ctx.FontEntries.TryGetValue(defaultFontNum, out FontEntry? fontEntry))
                        {
                            SymbolFont symbolFont = fontEntry.SymbolFont;
                            // Start at 1 because the "base" group is still inside an opening { so it's
                            // really group 1.
                            for (int i = 1; i < _groupCount + 1; i++)
                            {
                                int[] properties = _ctx.GroupStack.Properties[i];
                                int fontNum = properties[(int)Property.FontNum];
                                if (fontNum == NoFontNumber)
                                {
                                    properties[(int)Property.FontNum] = defaultFontNum;
                                    _ctx.GroupStack.SymbolFonts[i] = (byte)symbolFont;
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }

                        _inHandleFontTable = false;
                        return RtfError.OK;
                    }
                    break;
                case '\\':
                    RtfError ec = ParseKeyword();
                    if (ec != RtfError.OK) return ec;
                    break;
                case '\r':
                case '\n':
                    break;
                default:
                {
                    FontEntry? fontEntry = _ctx.FontEntries.Top;
                    if (!_ctx.GroupStack.CurrentSkipDest &&
                        // We can't check for codepage 42, because symbol fonts can have other codepages (although
                        // that may be a quirk/bug or whatever, but it can happen). Too bad, otherwise we could
                        // save time here...
                        fontEntry is { SymbolFont: SymbolFont.Unset })
                    {
                        _symbolFontNameBuffer.ClearFast();

                        int originalPos = CurrentPos;

                        // Increment the real position instead of a temp one, so that if we get an exception
                        // the error report will contain the real position (if we decide to report that in this
                        // version anyway).
                        for (int i = 0;
                             i < _maxSymbolFontNameLength && ch != ';';
                             i++, ch = (char)_rtfBytes[CurrentPos++])
                        {
                            _symbolFontNameBuffer.Add(ch);
                        }

                        CurrentPos = originalPos;

                        for (int i = _symbolArraysStartingIndex; i < _symbolArraysLength; i++)
                        {
                            byte[] nameBytes = _symbolFontCharsArrays[i];
                            if (SeqEqual(_symbolFontNameBuffer, nameBytes))
                            {
                                fontEntry.SymbolFont = (SymbolFont)i;
                                break;
                            }
                        }
                        if (fontEntry.SymbolFont == SymbolFont.Unset)
                        {
                            fontEntry.SymbolFont = SymbolFont.None;
                        }
                    }
                    break;
                }
            }
        }

        _inHandleFontTable = false;
        return RtfError.OK;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ChangeProperty(Property propertyTableIndex, int val)
    {
        if (propertyTableIndex == Property.FontNum)
        {
            if (_ctx.GroupStack.CurrentInFontTable)
            {
                _ctx.FontEntries.Add(val);
                return;
            }
            else if (_ctx.FontEntries.TryGetValue(val, out FontEntry? fontEntry))
            {
                if (fontEntry.CodePage == 42)
                {
                    // We have to track this globally, per behavior of RichEdit and implied by the spec.
                    _lastUsedFontWithCodePage42 = val;
                }

                _ctx.GroupStack.CurrentSymbolFont = fontEntry.SymbolFont;
            }
            // \fN supersedes \langN
            _ctx.GroupStack.CurrentProperties[(int)Property.Lang] = -1;
        }
        else if (propertyTableIndex == Property.Lang)
        {
            if (val == UndefinedLanguage) return;
        }

        _ctx.GroupStack.CurrentProperties[(int)propertyTableIndex] = val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RtfError ChangeDestination(DestinationType destinationType)
    {
        switch (destinationType)
        {
            case DestinationType.Skip:
                _ctx.GroupStack.CurrentSkipDest = true;
                return RtfError.OK;
            case DestinationType.FieldInstruction:
                return HandleFieldInstruction();
            // Stupid crazy type of control word, see description for enum field
            case DestinationType.CanBeDestOrNotDest:
            default:
                return RtfError.OK;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ParseChar_Explicit(char ch)
    {
        // No need to check for null, because only explicit chars will be passed (not unknown ones) and we know
        // none of them are null.
        if (_ctx.GroupStack.CurrentProperties[(int)Property.Hidden] == 0)
        {
            GroupStack groupStack = _ctx.GroupStack;
            SymbolFont symbolFont = groupStack.CurrentSymbolFont;
            if (symbolFont > SymbolFont.Unset)
            {
                uint[] fontTable = _symbolFontTables[(int)symbolFont];
                if (GetCharFromConversionList_UInt(ch, fontTable, out ListFast<char> result))
                {
                    _plainText.AddRange(result);
                }
            }
            else
            {
                _plainText.Add(ch);
            }
        }
    }

    #endregion

    #region Handle specially encoded characters

    #region Hex

    private void AddHexBuffer(bool success, bool codePageWas42, Encoding? enc, FontEntry? fontEntry)
    {
        // If multiple hex chars are directly after another (eg. \'81\'63) then they may be representing one
        // multibyte character (or not, they may also just be two single-byte chars in a row). To deal with
        // this, we have to put all contiguous hex chars into a buffer and when the run ends, we just pass
        // the buffer to the current encoding's byte-to-char decoder and get our correct result.

        ListFast<char> finalChars = _charGeneralBuffer;
        if (!success)
        {
            SetListFastToUnknownChar(finalChars);
            PutChars(finalChars, finalChars.Count);
        }
        else
        {
            // DON'T try to combine this byte with the next one if we're on code page 42 (symbol font translation) -
            // then we're guaranteed to be single-byte, and combining won't give a correct result
            if (codePageWas42)
            {
                if (fontEntry == null)
                {
                    for (int i = 0; i < _hexBuffer.Count; i++)
                    {
                        byte codePoint = _hexBuffer.ItemsArray[i];
                        GetCharFromConversionList_Byte(codePoint, _symbolFontTables[(int)SymbolFont.Symbol], out finalChars);
                        if (finalChars.Count == 0)
                        {
                            SetListFastToUnknownChar(finalChars);
                        }
                        PutChars(finalChars, finalChars.Count);
                    }
                }
                else
                {
                    SymbolFont symbolFont = fontEntry.SymbolFont;
                    if (symbolFont > SymbolFont.Unset)
                    {
                        for (int i = 0; i < _hexBuffer.Count; i++)
                        {
                            byte codePoint = _hexBuffer.ItemsArray[i];
                            GetCharFromConversionList_Byte(codePoint, _symbolFontTables[(int)symbolFont], out finalChars);
                            PutChars(finalChars, finalChars.Count);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < _hexBuffer.Count; i++)
                        {
                            try
                            {
                                if (enc != null)
                                {
                                    int sourceBufferCount = _hexBuffer.Count;
                                    finalChars.EnsureCapacity(sourceBufferCount);
                                    finalChars.Count = enc
                                        .GetChars(_hexBuffer.ItemsArray, 0, sourceBufferCount,
                                            finalChars.ItemsArray, 0);
                                }
                                else
                                {
                                    SetListFastToUnknownChar(finalChars);
                                }
                            }
                            catch
                            {
                                SetListFastToUnknownChar(finalChars);
                            }
                            PutChars(finalChars, finalChars.Count);
                        }
                    }
                }
            }
            else
            {
                try
                {
                    if (enc != null)
                    {
                        int sourceBufferCount = _hexBuffer.Count;
                        finalChars.EnsureCapacity(sourceBufferCount);
                        finalChars.Count = enc
                            .GetChars(_hexBuffer.ItemsArray, 0, sourceBufferCount, finalChars.ItemsArray, 0);
                    }
                    else
                    {
                        SetListFastToUnknownChar(finalChars);
                    }
                }
                catch
                {
                    SetListFastToUnknownChar(finalChars);
                }
                PutChars(finalChars, finalChars.Count);
            }
        }
    }

    private void HandleHexRun()
    {
        _hexBuffer.ClearFast();

        (bool success, bool codePageWas42, Encoding? enc, FontEntry? fontEntry) = GetCurrentEncoding();

        /*
        If the hex is invalid, oh well, we'll just get a junk character. No point in adding more branches
        to check for what's basically never going to happen.
        Other readers' behavior:
        -RichTextBox fails the whole read on invalid hex.
        -LibreOffice just skips invalid hex chars.
        If we wanted to match LibreOffice, we'd just only add to the buffer if neither nibble was 0xFF.
        Doing so makes us a little slower. Not catastrophically so, but meh.
        */
        byte b = _rtfBytes[CurrentPos++];
        byte hexNibble1 = _charToHex[b];
        b = _rtfBytes[CurrentPos++];
        byte hexNibble2 = _charToHex[b];
        byte finalHexByte = (byte)((hexNibble1 << 4) + hexNibble2);
        _hexBuffer.Add(finalHexByte);

        while (CurrentPos < _rtfBytes.Length)
        {
            b = _rtfBytes.Array[CurrentPos++];
            if (b == (byte)'\\')
            {
                b = _rtfBytes[CurrentPos++];
                if (b == (byte)'\'')
                {
                    b = _rtfBytes[CurrentPos++];
                    hexNibble1 = _charToHex[b];
                    b = _rtfBytes[CurrentPos++];
                    hexNibble2 = _charToHex[b];
                    finalHexByte = (byte)((hexNibble1 << 4) + hexNibble2);
                    _hexBuffer.Add(finalHexByte);
                }
                else
                {
                    CurrentPos -= 2;
                    AddHexBuffer(success, codePageWas42, enc, fontEntry);
                    return;
                }
            }
            // Spaces end a hex run, but linebreaks don't.
            else if (b is not (byte)'\r' and not (byte)'\n')
            {
                CurrentPos--;
                AddHexBuffer(success, codePageWas42, enc, fontEntry);
                return;
            }
        }
    }

    #endregion

    #region Unicode

    private RtfError HandleUnicodeRun()
    {
        while (CurrentPos < _rtfBytes.Length)
        {
            char ch = (char)_rtfBytes.Array[CurrentPos++];
            if (ch == '\\')
            {
                ch = (char)_rtfBytes[CurrentPos++];

                if (ch == 'u')
                {
                    ch = (char)_rtfBytes[CurrentPos++];

                    int negateParam = 0;
                    if (ch == '-')
                    {
                        negateParam = 1;
                        ch = (char)_rtfBytes[CurrentPos++];
                    }
                    if (ch.IsAsciiNumeric())
                    {
                        int param = 0;

                        checked
                        {
                            try
                            {
                                int i;
                                for (i = 0;
                                     i < ParamMaxLen + 1 && ch.IsAsciiNumeric();
                                     i++, ch = (char)_rtfBytes[CurrentPos++])
                                {
                                    param = (param * 10) + (ch - '0');
                                }
                                if (i > ParamMaxLen)
                                {
                                    return RtfError.ParameterOutOfRange;
                                }
                            }
                            catch (OverflowException)
                            {
                                return RtfError.ParameterOutOfRange;
                            }
                        }

                        param = BranchlessConditionalNegate(param, negateParam);

                        CurrentPos += MinusOneIfNotSpace_8Bits(ch);
                        HandleUnicodeParamAndSkipFallbackChars(param);
                    }
                    else
                    {
                        CurrentPos -= (3 + negateParam);
                        CurrentPos += MinusOneIfNotSpace_8Bits(ch);
                        ParseUnicode();
                        return RtfError.OK;
                    }
                }
                else
                {
                    CurrentPos -= 2;
                    ParseUnicode();
                    return RtfError.OK;
                }
            }
            else
            {
                CurrentPos--;
                ParseUnicode();
                return RtfError.OK;
            }
        }

        return RtfError.OK;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddCodePointToUnicodeBuffer(uint codePoint)
    {
        // If our code point has been through a font translation table, it may be longer than 2 bytes.
        if (codePoint > char.MaxValue)
        {
            ListFast<char>? chars = ConvertFromUtf32(codePoint, _charGeneralBuffer);
            if (chars == null)
            {
                _unicodeBuffer.Add(_unicodeUnknown_Char);
            }
            else
            {
                _unicodeBuffer.AddRange(chars, 2);
            }
        }
        else
        {
            _unicodeBuffer.Add((char)codePoint);
        }
    }

    private void HandleUnicodeParamAndSkipFallbackChars(int param)
    {
        // Make sure the code point is normalized before adding it to the buffer!
        NormalizeUnicodePoint_HandleSymbolCharRange(param, out uint codePoint);
        AddCodePointToUnicodeBuffer(codePoint);

        /*
        The spec states that, for the purposes of Unicode fallback character skipping, a "character" could mean
        any of the following:
        -An actual single character
        -A hex-encoded character (\'hh)
        -An entire control word
        -A \binN word, the space after it, and all of its binary data

        However, the Windows RichEdit control only counts raw chars and hex-encoded chars, so it doesn't conform
        to spec fully here. This is actually really fortunate, because ignoring the thorny "entire control word
        including bin and its data" thing means we get simpler and faster.
        */
        int numToSkip = _ctx.GroupStack.CurrentProperties[(int)Property.UnicodeCharSkipCount];
        while (numToSkip > 0 && CurrentPos < _rtfBytes.Length)
        {
            char c = (char)_rtfBytes.Array[CurrentPos++];
            switch (c)
            {
                case '\\':
                    if (CurrentPos < _rtfBytes.Length - 4 &&
                        _rtfBytes.Array[CurrentPos] == '\'' &&
                        _rtfBytes.Array[CurrentPos + 1].IsAsciiHex() &&
                        _rtfBytes.Array[CurrentPos + 2].IsAsciiHex())
                    {
                        CurrentPos += 3;
                        numToSkip--;
                    }
                    else if (CurrentPos < _rtfBytes.Length - 2 &&
                             _rtfBytes.Array[CurrentPos] is (byte)'{' or (byte)'}' or (byte)'\\')
                    {
                        CurrentPos++;
                        numToSkip--;
                    }
                    else
                    {
                        numToSkip--;
                    }
                    break;
                case '\r' or '\n':
                    break;
                // Per spec, if we encounter a group delimiter during Unicode skipping, we end skipping early
                case '{' or '}':
                    CurrentPos--;
                    return;
                default:
                    numToSkip--;
                    break;
            }
        }
    }

    private void ParseUnicode()
    {
        #region Handle surrogate pairs and fix up bad Unicode

        for (int i = 0; i < _unicodeBuffer.Count; i++)
        {
            char c = _unicodeBuffer.ItemsArray[i];

            if (char.IsHighSurrogate(c))
            {
                if (i < _unicodeBuffer.Count - 1 && char.IsLowSurrogate(_unicodeBuffer.ItemsArray[i + 1]))
                {
                    i++;
                }
                else
                {
                    _unicodeBuffer.ItemsArray[i] = _unicodeUnknown_Char;
                }
            }
            else if (char.IsLowSurrogate(c) || char.GetUnicodeCategory(c) is UnicodeCategory.OtherNotAssigned)
            {
                _unicodeBuffer.ItemsArray[i] = _unicodeUnknown_Char;
            }
        }

        #endregion

        PutChars(_unicodeBuffer, _unicodeBuffer.Count);

        _unicodeBuffer.ClearFast();
    }

    #endregion

    #region Field instructions

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FieldInst_GetFontNum()
    {
        int currentFontNumber = _ctx.GroupStack.CurrentProperties[(int)Property.FontNum];
        return currentFontNumber > NoFontNumber
            ? currentFontNumber
            : _ctx.Header.DefaultFontNum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FieldInst_AddChar(FontEntry fontEntry, ushort param)
    {
        // We already know our code point is within bounds of the array, because the arrays also go from
        // 0x20 - 0xFF, so no need to check.
        SymbolFont symbolFont = fontEntry.SymbolFont;
        if (symbolFont > SymbolFont.Unset)
        {
            uint codePoint = _symbolFontTables[(int)symbolFont][param - 0x20];
            AddCodePointToUnicodeBuffer(codePoint);
            ParseUnicode();
        }
        else
        {
            ListFast<char> finalChars = GetCharFromCodePage(-1, param);
            PutChars_FieldInst(finalChars, finalChars.Count);
        }
    }

    private void HandleFieldInst_A(ushort param)
    {
        if (param is < 0x20 or > byte.MaxValue) return;
        HandleFieldInst_PutCharWithEncoding(param);
    }

    private void HandleFieldInst_Unicode(ushort param)
    {
        if (param is >= 0xF020 and <= 0xF0FF)
        {
            param -= 0xF000;
        }

        if (param <= byte.MaxValue)
        {
            if (param >= 0x20)
            {
                int fontNum = FieldInst_GetFontNum();

                if (!_ctx.FontEntries.TryGetValue(fontNum, out FontEntry? fontEntry) || fontEntry.CodePage != 42)
                {
                    _plainText.Add((char)param);
                    return;
                }

                FieldInst_AddChar(fontEntry, param);
            }
        }
        else
        {
            _unicodeBuffer.Add((char)param);
            ParseUnicode();
        }
    }

    private void HandleFieldInst_J(ushort param) => HandleFieldInst_Unicode(param);

    private void HandleFieldInst_U(ushort param) => HandleFieldInst_Unicode(param);

    private void HandleFieldInst_PutCharWithEncoding(ushort param)
    {
        int fontNum = FieldInst_GetFontNum();

        if (!_ctx.FontEntries.TryGetValue(fontNum, out FontEntry? fontEntry))
        {
            ListFast<char> finalChars = GetCharFromCodePage(_ctx.Header.CodePage, param);
            PutChars_FieldInst(finalChars, finalChars.Count);
            return;
        }
        if (fontEntry.CodePage != 42)
        {
            ListFast<char> finalChars = GetCharFromCodePage(fontEntry.CodePage, param);
            PutChars_FieldInst(finalChars, finalChars.Count);
            return;
        }

        FieldInst_AddChar(fontEntry, param);
    }

    private void HandleFieldInst_F_Bare(ushort param)
    {
        if (param is >= 0xF020 and <= 0xF0FF)
        {
            param -= 0xF000;
        }

        if (param <= byte.MaxValue)
        {
            if (param >= 0x20)
            {
                HandleFieldInst_PutCharWithEncoding(param);
            }
        }
        else
        {
            _unicodeBuffer.Add((char)param);
            ParseUnicode();
        }
    }

    private void HandleFieldInst_F_WithSymbolFontName(ushort param, uint[] symbolFontTable)
    {
        uint codePoint = param;

        if (codePoint - 0xF020 <= 0xF0FF - 0xF020)
        {
            codePoint -= 0xF000;
        }
        if (GetCharFromConversionList_UInt(codePoint, symbolFontTable, out ListFast<char> finalChars) &&
            finalChars.Count > 0)
        {
            PutChars_FieldInst(finalChars, finalChars.Count);
        }
    }

    /*
    Field instructions are completely out to lunch with a totally unique syntax and even escaped control
    words (\\f etc.), so rather than try to shoehorn that into the regular parser and pollute it all up
    with incomprehensible nonsense, I'm just cordoning the whole thing off here. It's still incomprehensible
    nonsense, but hey, at least it stays in the loony bin and doesn't hurt anyone else.

    Also we only need one field instruction - SYMBOL. The syntax should go strictly like this:

    {\*\fldinst SYMBOL [\\f ["FontName"]] [\\a] [\\j] [\\u] [\\h] [\\s n] <arbitrary amounts of other junk>}
    
    With [] denoting optional parameters. I guess one of them must be present (at least we would fail if we
    didn't find any).
    
    Anyway, the spec is clear and simple and so we can just try to parse it exactly and quit on anything
    unexpected. Otherwise, we only want either \\f, \\a, \\j, or \\u. The others we ignore. Once we've found
    what we need, looped through six params and not found what we need, or reached a separator char, we quit
    and skip the rest of the group.

    Field instruction research:

    -Unicode character numbers can only be up to 2 bytes (0-65535), in other words "Unicode" here means "UTF-16".
     Field instructions can't be combined to produce 4 bytes the way \uN keywords can.
    -None support negative-and-add-65536.
    -None use "last used font even in a scope above"

    SYMBOL (num) \\f
    -------------------
    -Supports 0xF000-0xF0FF stuff

    SYMBOL (num) \\f "Symbol font name"
    -------------------
    -Ignores current font and "last used" font - it uses only the one specified in quotes
    -Supports 0xF000-0xF0FF stuff

    SYMBOL (num) \\a:
    -------------------
    -Doesn't support 0xF000-0xF0FF stuff
    -Supports dec or hex (single-byte)
    -If the current font is a symbol font, it displays in a symbol font and so still needs converting

    SYMBOL (num) \\j:
    -------------------
    -This is a character in Windows-31J (932), but it's specified with a Unicode codepoint, in either dec or hex.
    -Supports 0xF000-0xF0FF stuff (but maybe by accident of weird multi-byte behavior?)
    -Supports symbol fonts, even though it's a multibyte encoding...
    
    -These symbol-font-supporting things do a weird thing where if there's two bytes they ignore the second byte
     when they're in a symbol font, as far as I can tell? Like 0xF929 == 0x0929 == 0x0029.
     Except sometimes it isn't just the second byte, sometimes it's random and I don't know how it's arriving at
     the character. We should probably just say this is undefined behavior and not try to support it.

    SYMBOL (num) \\u:
    -------------------
    -Supports 0xF000-0xF0FF stuff (but maybe by accident of weird multi-byte behavior?)
     We should probably just say it doesn't support it because it doesn't make sense for Unicode.
    */
    private RtfError HandleFieldInstruction()
    {
        if (_ctx.GroupStack.CurrentProperties[(int)Property.Hidden] != 0) return RewindAndSkipGroup();

        _fldinstSymbolNumber.ClearFast();
        _fldinstSymbolFontName.ClearFast();

        ushort param;
        int i;

        #region Check for SYMBOL instruction

        if (CurrentPos > _rtfBytes.Length - 8) return RewindAndSkipGroup();

        // Compare the ulong-format "SYMBOL X" where X is any byte. Mask off the last byte, but it's
        // little-endian so the mask looks backwards.

        const ulong SYMBOLKeywordAsULong = 0x00204C4F424D5953;

        ulong SYMBOLKeyword = Unsafe.ReadUnaligned<ulong>(ref _rtfBytes.Array[CurrentPos]);

        SYMBOLKeyword &= 0x00FFFFFFFFFFFFFF;
        if (SYMBOLKeyword != SYMBOLKeywordAsULong)
        {
            // Manual return to match previous behavior more-or-less (don't rewind too far)
            _ctx.GroupStack.CurrentSkipDest = true;
            return RtfError.OK;
        }

        CurrentPos += 7;

        #endregion

        char ch = (char)_rtfBytes[CurrentPos++];

        bool numIsHex = false;

        #region Parse numeric field parameter

        if (ch == '-')
        {
            return RewindAndSkipGroup();
        }

        #region Handle if the param is hex

        if (ch == '0')
        {
            ch = (char)_rtfBytes[CurrentPos++];
            if (ch is 'x' or 'X')
            {
                ch = (char)_rtfBytes[CurrentPos++];
                if (ch == '-')
                {
                    return RewindAndSkipGroup();
                }
                numIsHex = true;
            }
        }

        #endregion

        #region Read parameter

        bool alphaCharsFound = false;
        bool alphaFound;
        for (i = 0;
             i < _fldinstSymbolNumberMaxLen && ((alphaFound = ch.IsAsciiAlpha()) || ch.IsAsciiNumeric());
             i++, ch = (char)_rtfBytes[CurrentPos++])
        {
            if (alphaFound) alphaCharsFound = true;

            _fldinstSymbolNumber.Add(ch);
        }

        if (_fldinstSymbolNumber.Count == 0 ||
            i >= _fldinstSymbolNumberMaxLen ||
            (!numIsHex && alphaCharsFound))
        {
            return RewindAndSkipGroup();
        }

        #endregion

        #region Parse parameter

        if (numIsHex)
        {
            // @ALLOC: ToString(): int.TryParse(hex)
            // We could implement our own hex parser, but this is called so infrequently (actually not at all
            // in the test set) that it doesn't really matter.
            // TODO: Make our own parser anyway, because speed in all things
            if (!ushort.TryParse(CreateStringFromChars(_fldinstSymbolNumber),
                    NumberStyles.HexNumber,
                    NumberFormatInfo.InvariantInfo,
                    out param))
            {
                return RewindAndSkipGroup();
            }
        }
        else
        {
            int parsed = ParseIntFast(_fldinstSymbolNumber);
            if (parsed <= ushort.MaxValue)
            {
                param = (ushort)parsed;
            }
            else
            {
                return RewindAndSkipGroup();
            }
        }

        #endregion

        #endregion

        if (ch != ' ') return RewindAndSkipGroup();

        const int maxParams = 6;

        ListFast<char> finalChars = _charGeneralBuffer;
        finalChars.Count = 0;

        #region Parse params

        for (i = 0; i < maxParams; i++)
        {
            ch = (char)_rtfBytes[CurrentPos++];
            if (ch != '\\') continue;
            ch = (char)_rtfBytes[CurrentPos++];
            if (ch != '\\') continue;

            ch = (char)_rtfBytes[CurrentPos++];

            // From the spec:
            // "Interprets text in field-argument as the value of an ANSI character."
            if (ch == 'a')
            {
                HandleFieldInst_A(param);
                return RewindAndSkipGroup();
            }
            /*
            From the spec:
            "Interprets text in field-argument as the value of a SHIFT-JIS character."

            Note that "SHIFT-JIS" in RTF land means specifically Windows-31J or whatever you want to call it.
            Also, the params here are just UTF-16 code points, the SHIFT-JIS encoding isn't really anything to
            do with it...
            */
            else if (ch == 'j')
            {
                HandleFieldInst_J(param);
                return RewindAndSkipGroup();
            }
            else if (ch == 'u')
            {
                HandleFieldInst_U(param);
                return RewindAndSkipGroup();
            }
            /*
            From the spec:
            "Interprets text in the switch's field-argument as the name of the font from which the character
            whose value is specified by text in the field's field-argument. By default, the font used is that
            for the current text run."

            In other words:
            If it's \\f, we use the current group's font's codepage, and if it's \\f "FontName" then we use
            code page 1252 and convert from FontName to Unicode using manual conversion tables.

            Note that RichEdit doesn't go this far. It reads the fldinst parts and all, and displays the
            characters in rich text if you have the appropriate fonts, but it doesn't convert from the fonts
            to equivalent Unicode chars when it converts to plain text. Which is reasonable really, but we
            want to do it because it's some cool ninja shit and also it helps us keep the odd copyright symbol
            intact and stuff.
            */
            else if (ch == 'f')
            {
                ch = (char)_rtfBytes[CurrentPos++];
                if (IsSeparatorChar(ch))
                {
                    HandleFieldInst_F_Bare(param);
                    return RewindAndSkipGroup();
                }
                else if (ch == ' ')
                {
                    ch = (char)_rtfBytes[CurrentPos++];
                    if (ch != '\"')
                    {
                        HandleFieldInst_F_Bare(param);
                        return RewindAndSkipGroup();
                    }

                    int fontNameCharCount = 0;

                    while ((ch = (char)_rtfBytes[CurrentPos++]) != '\"')
                    {
                        if (fontNameCharCount >= _maxSymbolFontNameLength || IsSeparatorChar(ch))
                        {
                            return RewindAndSkipGroup();
                        }
                        _fldinstSymbolFontName.Add(ch);
                        fontNameCharCount++;
                    }

                    for (int symbolFontI = _symbolArraysStartingIndex; symbolFontI < _symbolArraysLength; symbolFontI++)
                    {
                        byte[] symbolChars = _symbolFontCharsArrays[symbolFontI];
                        uint[] symbolFontTable = _symbolFontTables[symbolFontI];

                        if (SeqEqual(_fldinstSymbolFontName, symbolChars))
                        {
                            HandleFieldInst_F_WithSymbolFontName(param, symbolFontTable);
                            return RewindAndSkipGroup();
                        }
                    }
                }
            }
            /*
            From the spec:
            "Inserts the symbol without affecting the line spacing of the paragraph. If large symbols are
            inserted with this switch, text above the symbol may be overwritten."

            This doesn't concern us, so ignore it.
            */
            else if (ch == 'h')
            {
                ch = (char)_rtfBytes[CurrentPos++];
                if (IsSeparatorChar(ch)) break;
            }
            /*
            From the spec:
            "Interprets text in the switch's field-argument as the integral font size in points."

            This one takes an argument (hence the extra logic to ignore it), but, yeah, we ignore it.
            */
            else if (ch == 's')
            {
                ch = (char)_rtfBytes[CurrentPos++];
                if (ch != ' ') return RewindAndSkipGroup();

                int numDigitCount = 0;
                while ((ch = (char)_rtfBytes[CurrentPos++]).IsAsciiNumeric())
                {
                    if (numDigitCount > _fldinstSymbolNumberMaxLen)
                    {
                        return RewindAndSkipGroup();
                    }
                    numDigitCount++;
                }

                if (IsSeparatorChar(ch)) break;
            }
        }

        #endregion

        return RewindAndSkipGroup();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        RtfError RewindAndSkipGroup()
        {
            CurrentPos--;
            _ctx.GroupStack.CurrentSkipDest = true;
            return RtfError.OK;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsSeparatorChar(char ch) => ch is '\\' or '{' or '}';

        // Only call this if chars length is > 0 and consists solely of the characters '0' through '9'.
        // It does no checks at all and will throw if either of these things is false.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ParseIntFast(ListFast<char> chars)
        {
            int result = chars.ItemsArray[0] - '0';

            for (int i = 1; i < chars.Count; i++)
            {
                result *= 10;
                result += chars.ItemsArray[i] - '0';
            }
            return result;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ListFast<char> GetCharFromCodePage(int codePage, uint codePoint)
    {
        // BitConverter.GetBytes() does this, but it allocates a temp array every time.
        Unsafe.As<byte, uint>(ref _byteBuffer4[0]) = codePoint;

        try
        {
            if (codePage > -1)
            {
                _charGeneralBuffer.Count = GetEncodingFromCachedList(codePage)
                    .GetChars(_byteBuffer4, 0, 4, _charGeneralBuffer.ItemsArray, 0);
            }
            else
            {
                (bool success, _, Encoding? enc, _) = GetCurrentEncoding();
                if (success && enc != null)
                {
                    _charGeneralBuffer.Count = enc
                        .GetChars(_byteBuffer4, 0, 4, _charGeneralBuffer.ItemsArray, 0);
                }
                else
                {
                    SetListFastToUnknownChar(_charGeneralBuffer);
                }
            }
        }
        catch
        {
            SetListFastToUnknownChar(_charGeneralBuffer);
        }

        return _charGeneralBuffer;
    }

    #endregion

    #endregion

    #region PutChar

    private void PutChars(ListFast<char> ch, int count)
    {
        // This is only ever called from encoded-char handlers (hex, Unicode), so we don't need to duplicate any
        // of the bare-char symbol font stuff here.

        if (!(count == 1 && ch[0] == '\0') &&
            _ctx.GroupStack.CurrentProperties[(int)Property.Hidden] == 0 &&
            !_ctx.GroupStack.CurrentInFontTable)
        {
            _plainText.AddRange(ch, count);
        }
    }

    private void PutChars_FieldInst(ListFast<char> ch, int count)
    {
        for (int i = 0; i < count; i++)
        {
            char c = ch[i];
            if (c != '\0')
            {
                _plainText.Add(c);
            }
        }
    }

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string CreateStringFromChars(ListFast<char> chars) => new(chars.ItemsArray, 0, chars.Count);

    /// <summary>
    /// If <paramref name="codePage"/> is in the cached list, returns the Encoding associated with it;
    /// otherwise, gets the Encoding for <paramref name="codePage"/> and places it in the cached list
    /// for next time.
    /// </summary>
    /// <param name="codePage"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Encoding GetEncodingFromCachedList(int codePage) => codePage switch
    {
        _windows1252 => _windows1252Encoding,
        1250 => _windows1250Encoding,
        1251 => _windows1251Encoding,
        _shiftJisWin => _shiftJisWinEncoding,
        _ => _encodings.TryGetValue(codePage, out Encoding result)
            ? result
            // NOTE: This can throw, but all calls to this are wrapped in try-catch blocks.
            // TODO: But weird that we don't put the try-catch here and just return null...?
            : _encodings.AddAndReturn(codePage, GetEncoding_Arbitrary(codePage)),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (bool Success, bool CodePageWas42, Encoding? Encoding, FontEntry? FontEntry)
    GetCurrentEncoding()
    {
        int groupFontNum = _ctx.GroupStack.CurrentProperties[(int)Property.FontNum];
        int groupLang = _ctx.GroupStack.CurrentProperties[(int)Property.Lang];

        if (groupFontNum == NoFontNumber) groupFontNum = _ctx.Header.DefaultFontNum;

        _ctx.FontEntries.TryGetValue(groupFontNum, out FontEntry? fontEntry);

        int codePage;
        if (groupLang is > -1 and <= MaxLangNumIndex)
        {
            int translatedCodePage = LangToCodePage[groupLang];
            codePage = translatedCodePage > -1 ? translatedCodePage : fontEntry?.CodePage >= 0 ? fontEntry.CodePage : _ctx.Header.CodePage;
        }
        else
        {
            codePage = fontEntry?.CodePage >= 0 ? fontEntry.CodePage : _ctx.Header.CodePage;
        }

        if (codePage == 42) return (true, true, null, fontEntry);

        // Awful, but we're based on nice, relaxing error returns, so we don't want to throw exceptions. Ever.
        Encoding enc;
        try
        {
            enc = GetEncodingFromCachedList(codePage);
        }
        catch
        {
            try
            {
                enc = GetEncodingFromCachedList(_windows1252);
            }
            catch
            {
                return (false, false, null, null);
            }
        }

        return (true, false, enc, fontEntry);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GetCharFromConversionList_UInt(uint codePoint, uint[] fontTable, out ListFast<char> finalChars)
    {
        finalChars = _charGeneralBuffer;

        if (codePoint - 0x20 <= 0xFF - 0x20)
        {
            ListFast<char>? chars = ConvertFromUtf32(fontTable[codePoint - 0x20], _charGeneralBuffer);
            if (chars != null)
            {
                finalChars = chars;
            }
            else
            {
                SetListFastToUnknownChar(finalChars);
            }
        }
        else
        {
            if (codePoint > 255)
            {
                finalChars.Count = 0;
                return false;
            }
            try
            {
                _byteBuffer1[0] = (byte)codePoint;
                finalChars.Count = GetEncodingFromCachedList(_windows1252)
                    .GetChars(_byteBuffer1, 0, 1, finalChars.ItemsArray, 0);
            }
            catch
            {
                SetListFastToUnknownChar(finalChars);
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetCharFromConversionList_Byte(byte codePoint, uint[] fontTable, out ListFast<char> finalChars)
    {
        finalChars = _charGeneralBuffer;

        if (codePoint >= 0x20)
        {
            ListFast<char>? chars = ConvertFromUtf32(fontTable[codePoint - 0x20], _charGeneralBuffer);
            if (chars != null)
            {
                finalChars = chars;
            }
            else
            {
                SetListFastToUnknownChar(finalChars);
            }
        }
        else
        {
            try
            {
                _byteBuffer1[0] = codePoint;
                finalChars.Count = GetEncodingFromCachedList(_windows1252)
                    .GetChars(_byteBuffer1, 0, 1, finalChars.ItemsArray, 0);
            }
            catch
            {
                SetListFastToUnknownChar(finalChars);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NormalizeUnicodePoint_HandleSymbolCharRange(int codePoint, out uint returnCodePoint)
    {
        // Per spec, values >32767 are expressed as negative numbers, and we must add 65536 to get the
        // correct value.
        if (codePoint < 0)
        {
            codePoint += 65536;
            if (codePoint < 0)
            {
                returnCodePoint = _unicodeUnknown_Char;
                return;
            }
        }

        returnCodePoint = (uint)codePoint;

        /*
        From the spec:
        "Occasionally Word writes SYMBOL_CHARSET (nonUnicode) characters in the range U+F020..U+F0FF instead
        of U+0020..U+00FF. Internally Word uses the values U+F020..U+F0FF for these characters so that plain-
        text searches don't mistakenly match SYMBOL_CHARSET characters when searching for Unicode characters
        in the range U+0020..U+00FF. To find out the correct symbol font to use, e.g., Wingdings, Symbol,
        etc., find the last SYMBOL_CHARSET font control word \fN used, look up font N in the font table and
        find the face name. The charset is specified by the \fcharsetN control word and SYMBOL_CHARSET is for
        N = 2. This corresponds to codepage 42."

        Verified, this does in fact mean "find the last used font that specifically has \fcharset2" (or \cpg42).
        And, yes, that's last used, period, regardless of group. So we track it globally. That's the official
        behavior, don't ask me.

        Verified, these 0xF020-0xF0FF chars can be represented either as negatives or as >32767 positives
        (despite the spec saying that \uN must be signed int16). So we need to fall through to this section
        even if we did the above, because by adding 65536 we might now be in the 0xF020-0xF0FF range.
        */
        if (returnCodePoint - 0xF020 <= 0xF0FF - 0xF020)
        {
            returnCodePoint -= 0xF000;

            int fontNum = _lastUsedFontWithCodePage42 > NoFontNumber
                ? _lastUsedFontWithCodePage42
                : _ctx.Header.DefaultFontNum;

            if (!_ctx.FontEntries.TryGetValue(fontNum, out FontEntry? fontEntry) || fontEntry.CodePage != 42)
            {
                return;
            }

            // We already know our code point is within bounds of the array, because the arrays also go from
            // 0x20 - 0xFF, so no need to check.
            SymbolFont symbolFont = fontEntry.SymbolFont;
            if (symbolFont > SymbolFont.Unset)
            {
                returnCodePoint = _symbolFontTables[(int)symbolFont][returnCodePoint - 0x20];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SeqEqual(ListFast<char> seq1, byte[] seq2)
    {
        int seq1Count = seq1.Count;
        if (seq1Count != seq2.Length) return false;

        for (int ci = 0; ci < seq1Count; ci++)
        {
            if (seq1.ItemsArray[ci] != seq2[ci]) return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetListFastToUnknownChar(ListFast<char> list)
    {
        list.ItemsArray[0] = _unicodeUnknown_Char;
        list.Count = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleEndOfSkippableData()
    {
        if (_plainText.Count > 0 &&
            !char.IsWhiteSpace(_plainText[_plainText.Count - 1]))
        {
            _plainText.Add(' ');
        }
    }

    #endregion
}
