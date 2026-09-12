import type { ReactNode } from "react";

const shapes = {
  file: <><path d="M11.5 2.5h-7v15h11v-11z" /><path d="M11.5 2.5v4h4M7 10h6M7 13h5" /></>,
  plus: <path d="M10 4v12M4 10h12" />,
  panel: <><rect x="2.5" y="3.5" width="15" height="13" rx="2" /><path d="M7.5 3.5v13M4.5 7h1M4.5 10h1" /></>,
  chevron: <path d="m8 5 5 5-5 5" />,
  device: <><rect x="2.5" y="3" width="15" height="10.5" rx="1.5" /><path d="M7 17h6M10 13.5V17" /></>,
  text: <path d="M4 4.5h12M10 4.5v11M7 15.5h6" />,
  heading: <path d="M4 4v12M13 4v12M4 10h9M16 12v4" />,
  bulletList: <><path d="M8 5h9M8 10h9M8 15h9" /><path d="M3 5h.01M3 10h.01M3 15h.01" strokeWidth="3" /></>,
  orderedList: <><path d="M8 5h9M8 10h9M8 15h9M2 3h1v4M2 7h2M2 12c0-2 3-2 3 0l-3 4h3" /></>,
  taskList: <><rect x="2.5" y="3.5" width="15" height="13" rx="2" /><path d="m6 10 2.5 2.5L14 7" /></>,
  quote: <><path d="M3 5v10M7 6h10M7 10h10M7 14h7" /></>,
  comment: <><path d="M17 9.3a6.8 6.8 0 0 1-7 6.5c-1 0-2-.2-2.9-.6L3 17l.8-4A6.1 6.1 0 0 1 3 9.3a7 7 0 0 1 14 0Z" /><path d="M7 8h6M7 11h4" /></>,
  code: <path d="m6 5-4 5 4 5M14 5l4 5-4 5M11 4l-2 12" />,
  search: <><circle cx="8.5" cy="8.5" r="5.5" /><path d="m13 13 4 4" /></>,
  bold: <path d="M6 3.5h5a3.25 3.25 0 0 1 0 6.5H6m0-6.5v13h5.5a3.25 3.25 0 0 0 0-6.5H6" strokeWidth="1.9" />,
  italic: <path d="M9 3.5h6M5 16.5h6M12 3.5l-4 13" />,
  underline: <path d="M5 3.5v5a5 5 0 0 0 10 0v-5M4 17h12" />,
  strike: <path d="M14.5 5.5c-1-3.5-9-3-9 1 0 1.3 1 2.2 2.3 2.8M5.5 14.5c1 3.5 9 3 9-1 0-1.2-.8-2-2-2.7M3 10h14" />,
  highlight: <><path d="m8 12-2 4H3l3-6M7 11l5 3 5-9-5-3-5 9ZM4 18h12" /></>,
  link: <path d="m7.5 12.5 5-5M8 5l2-2a4.25 4.25 0 0 1 6 6l-2 2M12 15l-2 2a4.25 4.25 0 0 1-6-6l2-2" />,
  unlink: <path d="m8 5 2-2a4.25 4.25 0 0 1 6 6l-2 2M12 15l-2 2a4.25 4.25 0 0 1-6-6l2-2M2 2l16 16" />,
  eraser: <path d="m3 11 7-8 7 6-7 8H7l-4-3v-3ZM6.5 7l7 6M11 17h6" />,
  check: <path d="m4 10 4 4 8-8" />,
  close: <path d="m5 5 10 10M5 15 15 5" />,
  toggle: <><path d="m4 5 5 5-5 5M12 6h6M12 10h6M12 14h4" /></>,
  trash: <><path d="M3.5 5.5h13M8 3h4l1 2.5M5.5 5.5l1 12h7l1-12M8.5 8.5v6M11.5 8.5v6" /></>,
} satisfies Record<string, ReactNode>;

export type IconName = keyof typeof shapes;

export default function Icon({ name, size = 18 }: { name: IconName; size?: number }) {
  return <svg width={size} height={size} viewBox="0 0 20 20" fill="none" stroke="currentColor"
    strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{shapes[name]}</svg>;
}
