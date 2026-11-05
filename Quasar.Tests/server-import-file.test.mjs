import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(new URL('../Quasar/wwwroot/server-import-file.js', import.meta.url), 'utf8');
const { openFile } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
globalThis.DotNet = { createJSObjectReference: value => value, createJSStreamReference: value => value };

test('only the requested file slice is read; later input changes do not replace the snapshot', async () => {
    const requested = [];
    const file = new Blob(['unused-before', 'needed', 'unused-after']);
    const slice = file.slice.bind(file);
    file.slice = (start, end) => { requested.push([start, end]); return slice(start, end); };
    const input = { files: [file] };
    const selected = openFile(input);
    input.files = [];
    assert.equal(selected.getLength(), file.size);
    assert.equal(await selected.readRange(13, 6).text(), 'needed');
    assert.deepEqual(requested, [[13, 19]]);
});

test('invalid ranges and missing files fail before slicing', () => {
    assert.throws(() => openFile({ files: [] }), /Choose a ZIP/);
    let sliced = false;
    const selected = openFile({ files: [{ size: 10, slice: () => { sliced = true; } }] });
    for (const [offset, length] of [[-1, 2], [0, 0], [0, -1], [8, 3], [1.5, 2], [0, Infinity], [Number.MAX_SAFE_INTEGER + 1, 1]])
        assert.throws(() => selected.readRange(offset, length), /Invalid ZIP file range/);
    assert.equal(sliced, false);
});
