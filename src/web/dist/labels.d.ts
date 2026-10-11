/**
 * A label for an interaction, from markup only. Never reads what a user typed: inputs other than submit/button
 * values, textareas and selects are never consulted.
 */
export declare function labelOf(element: Element, kind: 'click' | 'submit', textLabels: boolean): {
    label: string;
    source: string;
};
