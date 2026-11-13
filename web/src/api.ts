export type Search = { text: string; area: string; remoteOnly: boolean };
export type Profile = { skills: string[]; text: string };
export type Company = {
  employerId: string;
  employees: number | null;
  rating: number | null;
  source: string | null;
  fromWebsite: boolean;
  reviews: number | null;
};
export type Vacancy = {
  id: string;
  title: string;
  employerId: string;
  employer: string;
  url: string;
  area: string;
  experience: string;
  remote: boolean;
  salaryFrom: number | null;
  salaryTo: number | null;
  currency: string | null;
  gross: boolean | null;
  publishedAt: string;
  requirement: string;
  responsibility: string;
  skills: string[];
  skillsVerified: boolean;
  publicationDateKnown: boolean;
  salaryPeriod: string;
  searchOrder: number;
};
export type Ranked = {
  vacancy: Vacancy;
  match: {
    score: number | null;
    matched: string[];
    missing: string[];
    required: string[];
  };
  company: Company | null;
};
export type State = {
  mode: "browser" | "demo";
  auth: HhAuthState;
  search: Search;
  profile: Profile;
  snapshot: {
    search: Search;
    syncedAt: string;
    found: number;
    truncated: boolean;
  } | null;
  sync: {
    running: boolean;
    lastAttempt: string | null;
    nextAttempt: string | null;
    error: string | null;
  };
  intervalSeconds: number;
};
export type HhAuthState = {
  stage: "disconnected" | "codeRequired" | "authenticated" | "manualAction";
  busy: boolean;
  emailHint: string | null;
  message: string | null;
  retryAt: string | null;
  canRestore: boolean;
};
export type Filter = {
  skills: string[];
  minScore: number;
  minSalary: number | null;
  currency: string;
  remoteOnly: boolean;
  minEmployees: number | null;
  maxEmployees: number | null;
  minRating: number | null;
  includeUnknownCompanies: boolean;
  employer: string;
  strictDotNet: boolean;
};
export const defaults: Filter = {
  skills: [],
  minScore: 0,
  minSalary: null,
  currency: "RUR",
  remoteOnly: false,
  minEmployees: null,
  maxEmployees: null,
  minRating: null,
  includeUnknownCompanies: true,
  employer: "",
  strictDotNet: true,
};

export async function api<T>(
  path: string,
  method = "GET",
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(`/api/${path}`, {
    method,
    signal,
    headers: { "Content-Type": "application/json" },
    body: method === "GET" ? undefined : JSON.stringify(body ?? {}),
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(
      problem.detail ||
        Object.values(problem.errors ?? {})
          .flat()
          .join(" ") ||
        `Ошибка сервера (${response.status})`,
    );
  }
  return response.status === 204 ? (undefined as T) : response.json();
}
export const splitSkills = (text: string) => [
  ...new Set(
    text
      .split(/[,;\n]/)
      .map((x) => x.trim())
      .filter(Boolean),
  ),
];
export const numberOrNull = (value: string) =>
  value === "" ? null : Number(value);
export const fmt = (value: number) =>
  new Intl.NumberFormat("ru-RU").format(value);
export function salary(v: Vacancy) {
  if (v.salaryFrom == null && v.salaryTo == null) return "Зарплата не указана";
  const currency =
    ({ RUR: "₽", USD: "$", EUR: "€" } as Record<string, string>)[
      v.currency ?? ""
    ] ??
    v.currency ??
    "";
  return `${
    v.salaryFrom != null && v.salaryTo != null
      ? `${fmt(v.salaryFrom)} – ${fmt(v.salaryTo)}`
      : v.salaryFrom != null
        ? `от ${fmt(v.salaryFrom)}`
        : `до ${fmt(v.salaryTo!)}`
  } ${currency}${v.salaryPeriod === "hour" ? " / час" : v.salaryPeriod === "shift" ? " / смену" : v.salaryPeriod === "day" ? " / день" : v.salaryPeriod === "unknown" ? " (период не указан)" : ""}`;
}
