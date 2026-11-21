const paths={
moon:'<path d="M20 15.2A8.5 8.5 0 018.8 4 8.5 8.5 0 1020 15.2Z"/>',
pack:'<path d="m3 7 9-4 9 4v10l-9 4-9-4V7Z"/><path d="m3 7 9 4 9-4M12 11v10M7.5 5l9 4"/>',
lines:'<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 10v2a7 7 0 0014 0v-2M12 19v3M9 22h6"/>',
looks:'<path d="m8 4-5 4 3 4 2-1v9h8v-9l2 1 3-4-5-4c0 4-8 4-8 0Z"/>',
translate:'<path d="M3 5h11M8 2v3M5 5c1 5 5 8 9 10M12 5c-1 5-5 8-9 10m10 7 4-11 4 11m-6.5-4h5"/>',
scenes:'<rect x="3" y="6" width="18" height="15" rx="2"/><path d="M3 11h18M6 6l3 5m3-5 3 5m3-5 3 5m-11 3 5 2.5-5 2.5Z"/>',
share:'<path d="M12 16V3m-5 5 5-5 5 5M4 13v7h16v-7"/>',
wiring:'<rect x="3" y="3" width="6" height="6" rx="1"/><rect x="15" y="15" width="6" height="6" rx="1"/><path d="M6 9v9h9M9 6h9v9"/>',
plus:'<path d="M12 5v14M5 12h14"/>',search:'<circle cx="10" cy="10" r="6"/><path d="m15 15 5 5"/>',
undo:'<path d="m8 4-5 5 5 5M3 9h11a6 6 0 010 12"/>',redo:'<path d="m16 4 5 5-5 5m5-5H10a6 6 0 000 12"/>',
play:'<path d="m8 4 12 8-12 8Z"/>',check:'<path d="m5 12 4 4L19 6"/>',arrow:'<path d="M4 12h16m-6-6 6 6-6 6"/>',
chevron:'<path d="m8 4 8 8-8 8"/>',down:'<path d="m5 8 7 7 7-7"/>',up:'<path d="m5 16 7-7 7 7"/>',close:'<path d="m6 6 12 12M6 18 18 6"/>',
headphones:'<path d="M3 15v-3a9 9 0 0118 0v3"/><rect x="3" y="12" width="4" height="8" rx="2"/><rect x="17" y="12" width="4" height="8" rx="2"/>',
sliders:'<path d="M4 7h16M4 17h16"/><circle cx="9" cy="7" r="3"/><circle cx="16" cy="17" r="3"/>',
log:'<path d="m4 6 5 5-5 5m8 1h8M3 21h18"/>',spark:'<path d="m12 2 2.8 7.2L22 12l-7.2 2.8L12 22l-2.8-7.2L2 12l7.2-2.8Z"/>',
shield:'<path d="m12 3 8 3v6c0 5-4 8-8 9-4-1-8-4-8-9V6Z"/><path d="m8 12 3 3 5-6"/>',
file:'<path d="M14 2H5v20h14V7l-5-5Z"/><path d="M14 2v5h5M8 12h8m-8 4h6"/>',trash:'<path d="M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7m4-7v7"/>',
stop:'<rect x="6" y="6" width="12" height="12" rx="2"/>',record:'<circle cx="12" cy="12" r="6" fill="currentColor" stroke="none"/>',download:'<path d="M12 3v12m-5-5 5 5 5-5M4 17v4h16v-4"/>',
cloud:'<path d="M6 18a5 5 0 01-1-10 7 7 0 0113-1 5.5 5.5 0 010 11Z"/>',info:'<circle cx="12" cy="12" r="9"/><path d="M12 11v6m0-10v1"/>',volume:'<path d="M11 4 5 9H2v6h3l6 5V4Zm4 4a6 6 0 010 8m3-11a10 10 0 010 14"/>'
};
export const icon=(name,cls='')=>`<svg class="icon ${cls}" aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">${paths[name]||paths.spark}</svg>`;
