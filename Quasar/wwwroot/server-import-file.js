// File.slice keeps unrelated ZIP contents on the user's PC. .NET owns ZIP parsing.
export function openFile(input) {
    const file = input?.files?.[0];
    if (!file) throw new Error("Choose a ZIP file first.");
    // InvokeAsync<IJSObjectReference> wraps this object in Blazor's reference table.
    return {
        getLength: () => file.size,
        readRange(offset, length) {
            if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length)
                || offset < 0 || length <= 0 || offset > file.size - length)
                throw new Error("Invalid ZIP file range.");
            // InvokeAsync<IJSStreamReference> likewise needs the raw Blob.
            return file.slice(offset, offset + length);
        }
    };
}
