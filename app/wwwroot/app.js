window.registerPasteHandler = (dotnetRef, selector) => {
    const textarea = document.querySelector(selector);
    if (!textarea) return;
    textarea.addEventListener('paste', async (e) => {
        e.preventDefault();
        const text = await dotnetRef.invokeMethodAsync('ReadClipboardText');
        if (!text) return;
        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;
        const before = textarea.value.substring(0, start);
        const after = textarea.value.substring(end);
        textarea.value = before + text + after;
        textarea.selectionStart = textarea.selectionEnd = start + text.length;
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
    });
};

window.onClickOutside = (dotnetRef, selector) => {
    const handler = (e) => {
        if (!e.target.closest(selector)) {
            dotnetRef.invokeMethodAsync('CloseDropdown');
            document.removeEventListener('click', handler, true);
        }
    };
    document.addEventListener('click', handler, true);
};
