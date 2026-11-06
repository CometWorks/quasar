import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(new URL('../Quasar/wwwroot/server-import-file.js', import.meta.url), 'utf8');
const { openFile } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
// Blazor wraps InvokeAsync<IJSObjectReference> and InvokeAsync<IJSStreamReference>
// results itself. Identity mocks hide accidental double-wrapping in the module.
const references = new Map();
globalThis.DotNet = {
    createJSObjectReference(value) {
        const id = references.size + 1;
        references.set(id, value);
        return { __jsObjectId: id };
    },
    createJSStreamReference(value) {
        assert.ok(value instanceof Blob, 'Blazor needs a raw Blob as the stream result');
        return { ...this.createJSObjectReference(value), __jsStreamReferenceLength: value.size };
    }
};

function openFileFromDotNet(input) {
    const reference = DotNet.createJSObjectReference(openFile(input));
    return references.get(reference.__jsObjectId);
}

function readRangeFromDotNet(selected, offset, length) {
    const reference = DotNet.createJSStreamReference(selected.readRange(offset, length));
    return references.get(reference.__jsObjectId);
}

test('only the requested file slice is read; later input changes do not replace the snapshot', async () => {
    const requested = [];
    const file = new Blob(['unused-before', 'needed', 'unused-after']);
    const slice = file.slice.bind(file);
    file.slice = (start, end) => { requested.push([start, end]); return slice(start, end); };
    const input = { files: [file] };
    const selected = openFileFromDotNet(input);
    input.files = [];
    assert.equal(selected.getLength(), file.size);
    assert.equal(await readRangeFromDotNet(selected, 13, 6).text(), 'needed');
    assert.deepEqual(requested, [[13, 19]]);
});

test('invalid ranges and missing files fail before slicing', () => {
    assert.throws(() => openFileFromDotNet({ files: [] }), /Choose a ZIP/);
    let sliced = false;
    const selected = openFileFromDotNet({ files: [{ size: 10, slice: () => { sliced = true; } }] });
    for (const [offset, length] of [[-1, 2], [0, 0], [0, -1], [8, 3], [1.5, 2], [0, Infinity], [Number.MAX_SAFE_INTEGER + 1, 1]])
        assert.throws(() => selected.readRange(offset, length), /Invalid ZIP file range/);
    assert.equal(sliced, false);
});
