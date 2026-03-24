window.onClickOutside = (dotnetRef, selector) => {
    const handler = (e) => {
        if (!e.target.closest(selector)) {
            dotnetRef.invokeMethodAsync('CloseDropdown');
            document.removeEventListener('click', handler, true);
        }
    };
    document.addEventListener('click', handler, true);
};
