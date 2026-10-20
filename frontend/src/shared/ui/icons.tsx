import type { SVGProps } from 'react';

/**
 * Referans tasarımdaki ince çizgili (1.75px) ikon dili. Harici ikon paketi eklenmedi: yalnızca kullanılan ikonlar burada.
 * Tümü dekoratiftir (aria-hidden); anlam taşıyan düğmeler kendi aria-label'ını verir.
 */
type IconProps = SVGProps<SVGSVGElement> & { size?: number };

function Icon({ size = 20, children, ...props }: IconProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      {...props}
    >
      {children}
    </svg>
  );
}

const make = (draw: React.ReactNode) =>
  function Glyph(props: IconProps) {
    return <Icon {...props}>{draw}</Icon>;
  };

export const ArrowLeftIcon = make(<path d="M19 12H5M11 18l-6-6 6-6" />);
export const ArrowRightIcon = make(<path d="M5 12h14M13 6l6 6-6 6" />);
export const CheckIcon = make(<path d="M5 12.5l4.5 4.5L19 7.5" />);
export const PlusIcon = make(<path d="M12 5v14M5 12h14" />);
export const CloseIcon = make(<path d="M6 6l12 12M18 6L6 18" />);
export const ChevronRightIcon = make(<path d="M9 6l6 6-6 6" />);
export const ChevronDownIcon = make(<path d="M6 9l6 6 6-6" />);
export const SearchIcon = make(
  <>
    <circle cx="11" cy="11" r="7" />
    <path d="M20 20l-4-4" />
  </>,
);
export const FilterIcon = make(<path d="M4 6h16M7 12h10M10 18h4" />);
export const TvIcon = make(
  <>
    <rect x="3" y="4" width="18" height="13" rx="2" />
    <path d="M8 21h8M12 17v4" />
  </>,
);
export const SparklesIcon = make(
  <path d="M12 3l1.8 4.6L18.5 9l-4.7 1.4L12 15l-1.8-4.6L5.5 9l4.7-1.4L12 3zM19 15l.8 2 2.2.7-2.2.7L19 20.5l-.8-2.1-2.2-.7 2.2-.7L19 15z" />,
);
export const CameraIcon = make(
  <>
    <path d="M4 8h3l2-3h6l2 3h3a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z" />
    <circle cx="12" cy="13" r="3.5" />
  </>,
);
export const CameraFlipIcon = make(
  <>
    <path d="M4 8h3l2-3h6l2 3h3a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z" />
    <path d="M9.5 13a2.5 2.5 0 0 1 4.3-1.7M14.5 13a2.5 2.5 0 0 1-4.3 1.7M13.5 10.5v1.2h-1.2M10.5 15.5v-1.2h1.2" />
  </>,
);
export const ImageIcon = make(
  <>
    <rect x="3" y="4" width="18" height="16" rx="2" />
    <circle cx="9" cy="10" r="1.5" />
    <path d="M21 16l-5-5-9 9" />
  </>,
);
export const ShieldIcon = make(
  <>
    <path d="M12 3l8 3v6c0 4.5-3.2 8.2-8 9-4.8-.8-8-4.5-8-9V6l8-3z" />
    <path d="M9 12l2 2 4-4" />
  </>,
);
export const TrashIcon = make(<path d="M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3" />);
export const HeartIcon = make(<path d="M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10z" />);
export const RefreshIcon = make(<path d="M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5" />);
export const InfoIcon = make(
  <>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 11v5M12 8h.01" />
  </>,
);
export const UserIcon = make(
  <>
    <circle cx="12" cy="8" r="4" />
    <path d="M4 20c0-4 3.6-6 8-6s8 2 8 6" />
  </>,
);
export const ClockIcon = make(
  <>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 7v5l3 2" />
  </>,
);
export const LayersIcon = make(<path d="M12 3l9 5-9 5-9-5 9-5zM3 13l9 5 9-5" />);
export const LogoutIcon = make(<path d="M9 4H5a1 1 0 0 0-1 1v14a1 1 0 0 0 1 1h4M16 8l4 4-4 4M20 12H9" />);
export const CompareIcon = make(<path d="M12 3v18M6 7l-3 5 3 5M18 7l3 5-3 5" />);
export const PencilIcon = make(<path d="M4 20l4-1 11-11-3-3L5 16l-1 4zM14 7l3 3" />);
export const UploadIcon = make(<path d="M12 16V4M7 9l5-5 5 5M4 20h16" />);
export const PaletteIcon = make(
  <path d="M12 3a9 9 0 1 0 0 18c1.5 0 2-1 2-2s-1-1.5-1-2.5 1-1.5 2-1.5h2a4 4 0 0 0 4-4c0-4-4-8-9-8z" />,
);
export const HistoryIcon = make(
  <>
    <path d="M3 12a9 9 0 1 0 3-6.7M3 4v5h5" />
    <path d="M12 7v5l3 2" />
  </>,
);
export const StoreIcon = make(<path d="M4 9l1-5h14l1 5M4 9v11h16V9M4 9a2.7 2.7 0 0 0 5.3 0 2.7 2.7 0 0 0 5.4 0A2.7 2.7 0 0 0 20 9M9 20v-6h6v6" />);
export const HangerIcon = make(
  <path d="M12 7a2.2 2.2 0 1 1 2.2 2.2c-1.2 0-2.2.9-2.2 2.1v.7l8.6 5.2c1.2.7.7 2.5-.7 2.5H4.1c-1.4 0-1.9-1.8-.7-2.5L12 12" />,
);
