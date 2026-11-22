import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(new URL('../Quasar/wwwroot/server-import-file.js', import.meta.url), 'utf8');
const { openFile } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
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

test('uses the ZIP central-directory hint for classic and ZIP64 archives', async () => {
    const bytes = new Uint8Array(300);
    const view = new DataView(bytes.buffer);
    const end = bytes.length - 22;
    view.setUint32(end, 0x06054b50, true);
    view.setUint32(end + 16, 90, true);
    const hint = () => openFile({files:[new Blob([bytes])]}).directoryOffset();
    assert.equal(await hint(), 90);
    view.setUint32(end + 16, 0xffffffff, true);
    view.setUint32(end - 20, 0x07064b50, true);
    view.setBigUint64(end - 12, 190n, true);
    view.setUint32(190, 0x06064b50, true);
    view.setBigUint64(238, 80n, true);
    assert.equal(await hint(), 80);
    view.setBigUint64(238, 0xffffffffffffffffn, true);
    assert.equal(await hint(), bytes.length);
    view.setUint16(end + 20, 1, true);
    assert.equal(await hint(), bytes.length);
});
