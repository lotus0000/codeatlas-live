/**
 * A label for an interaction, from markup only. Never reads what a user typed: inputs other than submit/button
 * values, textareas and selects are never consulted.
 */
export function labelOf(element, kind, textLabels) {
    const explicit = element.getAttribute('data-codeatlas');
    if (explicit)
        return { label: explicit, source: 'data-codeatlas' };
    const aria = element.getAttribute('aria-label');
    if (aria)
        return { label: aria, source: 'aria-label' };
    if (kind === 'submit') {
        const name = element.getAttribute('name') || element.id;
        return { label: name ? `form ${name}` : 'form', source: 'form-name' };
    }
    if (textLabels) {
        const text = element instanceof HTMLInputElement
            ? (element.type === 'submit' || element.type === 'button' ? element.value : '')
            : element.textContent ?? '';
        const label = text.replace(/\s+/g, ' ').trim().slice(0, 40);
        if (label)
            return { label, source: 'text' };
    }
    return { label: element.tagName.toLowerCase(), source: 'tag' };
}
