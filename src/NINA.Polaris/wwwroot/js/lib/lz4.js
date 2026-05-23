/*
 * Minimal LZ4 block-format decompressor — pure JS, no dependencies.
 *
 * Polaris's server (K4os.Compression.LZ4 → LZ4Codec.Encode L00_FAST)
 * emits raw LZ4 block streams: no magic numbers, no checksums, no
 * frame headers. The uncompressed length is carried out-of-band in
 * the WebSocket frame header, so we only need block-level decode.
 *
 * Spec: https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
 *
 * Exposes globalThis.LZ4.decompress(src: Uint8Array, dst: Uint8Array).
 * Caller is responsible for sizing dst to the known uncompressed
 * length (we have it from the stream header).
 *
 * Total size ~1 KB minified — way smaller than vendoring lz4js.
 */
(function (global) {
    'use strict';

    function decompress(src, dst) {
        let sIdx = 0;
        let dIdx = 0;
        const sLen = src.length;
        const dLen = dst.length;

        while (sIdx < sLen) {
            // Token: high nibble = literal length, low nibble = match length.
            const token = src[sIdx++];
            let literalLen = token >>> 4;
            let matchLen = (token & 0x0F) + 4;

            // Literal-length extension (0xFF chain).
            if (literalLen === 15) {
                let b;
                do {
                    b = src[sIdx++];
                    literalLen += b;
                } while (b === 255 && sIdx < sLen);
            }

            // Copy literals straight through.
            if (literalLen > 0) {
                if (dIdx + literalLen > dLen) {
                    throw new Error('LZ4: literal overflow at dst ' + dIdx);
                }
                // Use set() for the bulk-copy fast path.
                dst.set(src.subarray(sIdx, sIdx + literalLen), dIdx);
                dIdx += literalLen;
                sIdx += literalLen;
            }

            // End of block: last sequence has no match section.
            if (sIdx >= sLen) break;

            // 2-byte little-endian offset.
            const offset = src[sIdx++] | (src[sIdx++] << 8);
            if (offset === 0) {
                throw new Error('LZ4: zero offset at src ' + (sIdx - 2));
            }

            // Match-length extension.
            if (matchLen - 4 === 15) {
                let b;
                do {
                    b = src[sIdx++];
                    matchLen += b;
                } while (b === 255 && sIdx < sLen);
            }

            // Byte-by-byte copy from earlier in dst. CAN overlap forward
            // (offset < matchLen) — that's the LZ4 RLE trick used to
            // expand repeated runs, so we MUST step one byte at a time
            // rather than use Uint8Array.set() which would snapshot the
            // source first.
            if (dIdx + matchLen > dLen) {
                throw new Error('LZ4: match overflow at dst ' + dIdx);
            }
            const mStart = dIdx - offset;
            if (mStart < 0) {
                throw new Error('LZ4: negative match offset at dst ' + dIdx);
            }
            for (let i = 0; i < matchLen; i++) {
                dst[dIdx++] = dst[mStart + i];
            }
        }

        return dIdx;
    }

    global.LZ4 = { decompress: decompress };
})(typeof globalThis !== 'undefined' ? globalThis : window);
