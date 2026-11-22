// File.slice keeps unrelated ZIP contents on the user's PC. .NET owns ZIP parsing.
export function openFile(input) {
    const file = input?.files?.[0];
    if (!file) throw new Error("Choose a ZIP file first.");
    return {
        getLength: () => file.size,
        async directoryOffset() {
            // This is a read-ahead hint only. The worker validates the directory, paths, sizes and checkpoints.
            // Starting at the exact end record avoids reading unrelated payload when the ZIP has no comment.
            const integer = async (start, length) => new DataView(await file.slice(start, start + length).arrayBuffer());
            if (file.size < 22) return file.size;
            const end = await integer(file.size - 22, 22);
            if (end.getUint32(0, true) !== 0x06054b50 || end.getUint16(20, true)) return file.size;
            const offset = end.getUint32(16, true);
            if (offset !== 0xffffffff) return offset <= file.size ? offset : file.size;
            if (file.size < 42) return file.size;
            const locator = await integer(file.size - 42, 20);
            if (locator.getUint32(0, true) !== 0x07064b50) return file.size;
            const zip64 = Number(locator.getBigUint64(8, true));
            if (!Number.isSafeInteger(zip64) || zip64 > file.size - 56) return file.size;
            const record = await integer(zip64, 56);
            if (record.getUint32(0, true) !== 0x06064b50) return file.size;
            const directory = Number(record.getBigUint64(48, true));
            return Number.isSafeInteger(directory) && directory <= file.size ? directory : file.size;
        },
        readRange(offset, length) {
            if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length)
                || offset < 0 || length <= 0 || offset > file.size - length)
                throw new Error("Invalid ZIP file range.");
            return file.slice(offset, offset + length);
        }
    };
}
