import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import {
  ArrowDownWideNarrow,
  ArrowUpRight,
  BriefcaseBusiness,
  Check,
  ChevronDown,
  CircleHelp,
  Crosshair,
  FileText,
  MapPin,
  RefreshCw,
  Search as SearchIcon,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  X,
} from "lucide-react";
import { api, defaults, fmt, numberOrNull, salary, splitSkills } from "./api";
import type { Company, Filter, Ranked, Search, State } from "./api";
import HhAuthPanel from "./HhAuthPanel";
import CoverLetterDialog from "./CoverLetterDialog";
import type { Vacancy } from "./api";

function Modal({
  title,
  children,
  close,
}: {
  title: string;
  children: ReactNode;
  close: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current!;
    dialog.showModal();
    return () => dialog.close();
  }, []);
  return (
    <dialog
      ref={ref}
      onCancel={close}
      onClick={(e) => {
        if (e.target === e.currentTarget) close();
      }}
    >
      <div className="modal-heading">
        <h2>{title}</h2>
        <button className="icon-button" onClick={close} aria-label="Закрыть">
          <X size={20} />
        </button>
      </div>
      {children}
    </dialog>
  );
}

export default function App() {
  const [state, setState] = useState<State | null>(null);
  const [search, setSearch] = useState<Search>({
    text: ".NET",
    area: "113",
    remoteOnly: false,
  });
  const initialized = useRef(false);
  const [filter, setFilter] = useState<Filter>(defaults);
  const [requiredText, setRequiredText] = useState("");
  const [items, setItems] = useState<Ranked[]>([]);
  const [cached, setCached] = useState(0);
  const [revision, setRevision] = useState(0);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [queryError, setQueryError] = useState("");
  const [notice, setNotice] = useState("");
  const [modal, setModal] = useState<"profile" | "help" | null>(null);
  const [letterVacancy, setLetterVacancy] = useState<Vacancy | null>(null);
  const [skills, setSkills] = useState("");
  const [resume, setResume] = useState("");
  const [company, setCompany] = useState<{
    name: string;
    info: Company;
  } | null>(null);
  const [visible, setVisible] = useState(30);
  const [tick, setTick] = useState(Date.now());
  const patch = (part: Partial<Filter>) => {
    setFilter((f) => ({ ...f, ...part }));
    setVisible(30);
  };

  const refresh = useCallback(async () => {
    try {
      const next = await api<State>("state");
      setState(next);
      if (!initialized.current) {
        setSearch(next.search);
        initialized.current = true;
      }
      setError("");
      setRevision((v) => v + 1);
    } catch (e) {
      setError(
        e instanceof Error
          ? e.message
          : "Backend недоступен. Запустите .NET API.",
      );
    }
  }, []);

  useEffect(() => {
    void refresh();
    const poll = window.setInterval(() => void refresh(), 10_000);
    const clock = window.setInterval(() => setTick(Date.now()), 1000);
    return () => {
      clearInterval(poll);
      clearInterval(clock);
    };
  }, [refresh]);

  useEffect(() => {
    const abort = new AbortController();
    const timeout = setTimeout(() => {
      setLoading(true);
      api<{ items: Ranked[]; cached: number }>(
        "vacancies/query",
        "POST",
        filter,
        abort.signal,
      )
        .then((result) => {
          setItems(result.items);
          setCached(result.cached);
          setQueryError("");
        })
        .catch((e) => {
          if (e.name !== "AbortError") setQueryError(e.message);
        })
        .finally(() => {
          if (!abort.signal.aborted) setLoading(false);
        });
    }, 250);
    return () => {
      clearTimeout(timeout);
      abort.abort();
    };
  }, [filter, revision]);

  async function sync(e?: FormEvent) {
    e?.preventDefault();
    setBusy(true);
    setNotice("");
    try {
      await api("search", "PUT", search);
      await api("sync", "POST");
      await refresh();
    } catch (e) {
      setNotice((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  function openProfile() {
    setSkills(state?.profile.skills.join(", ") ?? "");
    setResume(state?.profile.text ?? "");
    setNotice("");
    setModal("profile");
  }

  async function saveProfile(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api("profile", "PUT", {
        skills: splitSkills(skills),
        text: resume,
      });
      await refresh();
      setModal(null);
      setNotice("Профиль сохранён. Соответствие пересчитано.");
    } catch (e) {
      setNotice((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function saveCompany(e: FormEvent) {
    e.preventDefault();
    if (!company) return;
    setBusy(true);
    try {
      await api(`companies/${company.info.employerId}`, "PUT", {
        ...company.info,
        fromWebsite: false,
      });
      setCompany(null);
      await refresh();
      setNotice("Сведения о компании сохранены локально.");
    } catch (e) {
      setNotice((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const running = busy || state?.sync.running;
  const hasProfile =
    !!state &&
    (state.profile.skills.length > 0 || state.profile.text.trim().length > 0);
  const remaining = state?.sync.nextAttempt
    ? Math.max(0, Math.ceil((Date.parse(state.sync.nextAttempt) - tick) / 1000))
    : 0;
  const countdown = `${Math.floor(remaining / 60)}:${String(remaining % 60).padStart(2, "0")}`;
  const snapshotChanged =
    state?.snapshot &&
    JSON.stringify(state.snapshot.search) !== JSON.stringify(state.search);
  const activeFilters =
    filter.skills.length +
    Number(filter.minScore > 0) +
    Number(filter.minSalary != null) +
    Number(filter.remoteOnly) +
    Number(!!filter.employer) +
    Number(filter.minRating != null) +
    Number(filter.minEmployees != null || filter.maxEmployees != null);

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a className="brand" href="/" aria-label="Stack Scout — главная">
          <span className="brand-mark">
            <Crosshair size={24} />
          </span>
          <span>
            stack<span className="brand-light">scout</span>
            <small>ВАШ СТЕК. ВАША РАБОТА.</small>
          </span>
        </a>
        <div className="workspace-label">ЛИЧНОЕ ПРОСТРАНСТВО</div>
        <nav aria-label="Навигация">
          <button
            className="nav-item selected"
            onClick={() => window.scrollTo({ top: 0, behavior: "smooth" })}
          >
            <BriefcaseBusiness size={19} />
            Вакансии<span className="nav-count">{items.length}</span>
          </button>
          <button className="nav-item" onClick={openProfile}>
            <FileText size={19} />
            Мои навыки
            <span className="tiny-dot" />
          </button>
          <button className="nav-item" onClick={() => setModal("help")}>
            <CircleHelp size={19} />
            Как это работает
          </button>
        </nav>
        <div className="sidebar-note">
          <div className="note-icon">
            <ShieldCheck size={21} />
          </div>
          <strong>Сессия на вашем компьютере</strong>
          <p>
            Вход по почте и коду. Браузерный профиль и резюме хранятся локально.
          </p>
          <span className="source-label">
            <span />
            {state?.mode === "demo"
              ? "Демонстрационный режим"
              : "Чтение страниц через браузер"}
          </span>
        </div>
        <div className="sidebar-footer">
          <span className="avatar">Л</span>
          <div>
            <strong>Локальный профиль</strong>
            <small>Только на вашем компьютере</small>
          </div>
        </div>
      </aside>

      <main>
        <header className="topbar">
          <div>
            <span className="breadcrumb">Рабочее пространство</span>
            <span className="slash">/</span>Вакансии
          </div>
          <div className="local-badge">
            <span />
            Локальный режим
          </div>
        </header>
        <div className="content">
          <section className="page-heading">
            <div>
              <div className="eyebrow">ТОЧНО В ВАШ СТЕК</div>
              <h1>
                Работа, которая подходит<span>.</span>
              </h1>
              <p>Вакансии C# и .NET. Больше точных совпадений, меньше шума.</p>
            </div>
            <button
              className="button secondary profile-button"
              onClick={openProfile}
            >
              <FileText size={17} />
              Мои навыки
              <ArrowUpRight size={16} />
            </button>
          </section>

          {state?.mode === "browser" && (
            <HhAuthPanel
              auth={state.auth}
              blocked={!!running}
              refresh={refresh}
              connected={sync}
            />
          )}
          <form className="search-bar" onSubmit={sync}>
            <div className="search-input">
              <SearchIcon size={20} />
              <input
                aria-label="Поисковый запрос hh.ru"
                value={search.text}
                onChange={(e) => setSearch({ ...search, text: e.target.value })}
                required
                maxLength={200}
                placeholder="Должность или технология"
              />
            </div>
            <div className="region-input">
              <MapPin size={18} />
              <select
                aria-label="Регион поиска"
                value={search.area}
                onChange={(e) => setSearch({ ...search, area: e.target.value })}
              >
                <option value="113">Вся Россия</option>
                <option value="1">Москва</option>
                <option value="2">Санкт-Петербург</option>
                <option value="">Все регионы</option>
              </select>
            </div>
            <button
              className="button primary"
              disabled={
                !!running ||
                !state ||
                (state.mode === "browser" &&
                  state.auth.stage !== "authenticated")
              }
              type="submit"
            >
              <RefreshCw size={17} className={running ? "spin" : ""} />
              {running ? "Обновляем…" : "Найти вакансии"}
            </button>
          </form>

          <section
            className={`focus-banner ${filter.strictDotNet ? "" : "relaxed"}`}
          >
            <div className="focus-icon">
              <Crosshair size={23} />
            </div>
            <div>
              <strong>
                {filter.strictDotNet ? "Фокус на C# / .NET" : "Свободный поиск"}
              </strong>
              <p>
                {filter.strictDotNet
                  ? "Другой основной стек исключается до оценки соответствия."
                  : "Показываем все стеки из результатов поиска."}
              </p>
            </div>
            <label className="switch">
              <input
                type="checkbox"
                aria-label="Только C# и .NET"
                checked={filter.strictDotNet}
                onChange={(e) => patch({ strictDotNet: e.target.checked })}
              />
              <span />
            </label>
          </section>

          {state?.mode === "demo" && (
            <div className="alert demo-banner">
              <strong>Демонстрационный режим.</strong> Компании и вакансии
              вымышлены. Для настоящих данных запустите приложение без -Demo и
              войдите по почте и коду.
            </div>
          )}
          {(error || notice || queryError || state?.sync.error) && (
            <div
              role="status"
              className={`alert ${error || queryError || state?.sync.error ? "warning" : ""}`}
            >
              {error
                ? `API недоступен: ${error}. Проверьте, что backend запущен.`
                : queryError || state?.sync.error || notice}
            </div>
          )}
          {snapshotChanged && (
            <div className="alert warning">
              Показан предыдущий поиск «{state?.snapshot?.search.text}». Новый
              список появится после успешного обновления.
            </div>
          )}

          <div className="results-layout">
            <aside className="filters" aria-label="Фильтры вакансий">
              <div className="filter-heading">
                <h2>
                  <SlidersHorizontal size={17} />
                  Фильтры {activeFilters > 0 && <span>{activeFilters}</span>}
                </h2>
                <button
                  className="text-button"
                  onClick={() => {
                    setFilter(defaults);
                    setRequiredText("");
                    setVisible(30);
                  }}
                >
                  Сбросить
                </button>
              </div>
              <div className="filter-group">
                <label htmlFor="required-skills">Обязательные навыки</label>
                <input
                  id="required-skills"
                  value={requiredText}
                  onChange={(e) => {
                    setRequiredText(e.target.value);
                    patch({ skills: splitSkills(e.target.value) });
                  }}
                  placeholder="C#, SQL, Docker"
                />
                <p className="hint">Через запятую. Нужны все навыки.</p>
                <div className="quick-skills">
                  {["C#", ".NET", "PostgreSQL", "React", "Docker"].map(
                    (skill) => (
                      <button
                        key={skill}
                        className={
                          filter.skills.includes(skill) ? "active" : ""
                        }
                        onClick={() => {
                          const next = filter.skills.includes(skill)
                            ? filter.skills.filter((s) => s !== skill)
                            : [...filter.skills, skill];
                          setRequiredText(next.join(", "));
                          patch({ skills: next });
                        }}
                      >
                        {filter.skills.includes(skill) && <Check size={11} />}
                        {skill}
                      </button>
                    ),
                  )}
                </div>
              </div>
              <div className="filter-group">
                <label htmlFor="match-score">
                  Соответствие навыкам{" "}
                  <b>
                    {filter.minScore > 0 ? `от ${filter.minScore}%` : "Любое"}
                  </b>
                </label>
                <input
                  id="match-score"
                  type="range"
                  min="0"
                  max="100"
                  step="5"
                  value={filter.minScore}
                  onChange={(e) => patch({ minScore: +e.target.value })}
                />
                <div className="range-captions">
                  <span>0%</span>
                  <span>100%</span>
                </div>
              </div>
              <div className="filter-group">
                <label htmlFor="salary">Зарплата в месяц</label>
                <div className="salary-filter">
                  <input
                    id="salary"
                    type="number"
                    min="0"
                    step="10000"
                    placeholder="от 200 000"
                    value={filter.minSalary ?? ""}
                    onChange={(e) =>
                      patch({ minSalary: numberOrNull(e.target.value) })
                    }
                  />
                  <select
                    aria-label="Валюта зарплаты"
                    value={filter.currency}
                    onChange={(e) => patch({ currency: e.target.value })}
                  >
                    <option value="RUR">₽</option>
                    <option value="USD">$</option>
                    <option value="EUR">€</option>
                  </select>
                </div>
                <p className="hint">
                  По диапазону вакансии, без пересчёта налогов.
                </p>
              </div>
              <div className="filter-group">
                <label className="checkbox">
                  <input
                    type="checkbox"
                    checked={filter.remoteOnly}
                    onChange={(e) => patch({ remoteOnly: e.target.checked })}
                  />
                  Только удалённая работа
                </label>
              </div>
              <div className="filter-group">
                <label htmlFor="employer">Компания</label>
                <input
                  id="employer"
                  placeholder="Название компании"
                  value={filter.employer}
                  onChange={(e) => patch({ employer: e.target.value })}
                  maxLength={200}
                />
                <details className="company-filters">
                  <summary>
                    Размер и оценка
                    <ChevronDown size={14} />
                  </summary>
                  <p className="hint">
                    Рейтинг читается со страницы hh.ru, если он указан. Размер
                    компании можно добавить вручную.
                  </p>
                  <label htmlFor="employees-min">Сотрудников</label>
                  <div className="input-pair">
                    <input
                      id="employees-min"
                      type="number"
                      min="0"
                      placeholder="От"
                      value={filter.minEmployees ?? ""}
                      onChange={(e) =>
                        patch({ minEmployees: numberOrNull(e.target.value) })
                      }
                    />
                    <input
                      aria-label="Максимум сотрудников"
                      type="number"
                      min="0"
                      placeholder="До"
                      value={filter.maxEmployees ?? ""}
                      onChange={(e) =>
                        patch({ maxEmployees: numberOrNull(e.target.value) })
                      }
                    />
                  </div>
                  <label htmlFor="rating">Рейтинг / оценка, от</label>
                  <select
                    id="rating"
                    value={filter.minRating ?? ""}
                    onChange={(e) =>
                      patch({ minRating: numberOrNull(e.target.value) })
                    }
                  >
                    <option value="">Любая</option>
                    <option value="3">3 из 5</option>
                    <option value="4">4 из 5</option>
                    <option value="4.5">4,5 из 5</option>
                  </select>
                  <label className="checkbox">
                    <input
                      type="checkbox"
                      checked={filter.includeUnknownCompanies}
                      onChange={(e) =>
                        patch({ includeUnknownCompanies: e.target.checked })
                      }
                    />
                    Включать компании без данных
                  </label>
                </details>
              </div>
              <div className="filter-footnote">
                <ShieldCheck size={15} />
                <span>Фильтры применяются к загруженному списку.</span>
              </div>
            </aside>

            <section className="results" aria-label="Результаты поиска">
              <div className="results-heading">
                <div>
                  <h2>
                    Подходящие вакансии <span>{items.length}</span>
                  </h2>
                  <p>
                    {state?.snapshot ? (
                      <>
                        Обновлено{" "}
                        {new Date(state.snapshot.syncedAt).toLocaleTimeString(
                          "ru-RU",
                          { hour: "2-digit", minute: "2-digit" },
                        )}
                        <span className="dot-divider">·</span>
                        {running
                          ? "Синхронизация…"
                          : `Следующая проверка ${remaining > 0 ? `через ${countdown}` : "скоро"}`}
                      </>
                    ) : running ? (
                      "Загружаем вакансии из hh.ru…"
                    ) : (
                      "Ожидаем первую загрузку"
                    )}
                  </p>
                </div>
                <div className="sort-label">
                  <ArrowDownWideNarrow size={16} />
                  <span>По соответствию</span>
                </div>
              </div>
              {!hasProfile && (
                <button className="profile-nudge" onClick={openProfile}>
                  <Sparkles size={19} />
                  <span>
                    <strong>Добавьте ваши навыки</strong> — и увидите совпадения
                    по каждой вакансии.
                  </span>
                  <ArrowUpRight size={17} />
                </button>
              )}
              {state?.snapshot?.truncated && (
                <p className="limit-note">
                  Загружено {cached} из {fmt(state.snapshot.found)} вакансий
                  hh.ru. Уточните поисковый запрос, чтобы охватить нужный стек.
                </p>
              )}
              {loading && items.length === 0 ? (
                <div className="empty-state">
                  <RefreshCw size={28} className="spin" />
                  <h3>Загружаем список</h3>
                  <p>Получаем вакансии из локального API.</p>
                </div>
              ) : items.length === 0 ? (
                <div className="empty-state">
                  <SearchIcon size={32} />
                  <h3>
                    {cached > 0
                      ? "Точных совпадений пока нет"
                      : "Здесь появятся ваши вакансии"}
                  </h3>
                  <p>
                    {cached > 0
                      ? "Попробуйте убрать обязательный навык или снизить порог соответствия. Строгий режим скрывает вакансии без подтверждения стека."
                      : state?.mode === "browser"
                        ? "Подключите hh.ru по почте и коду, затем нажмите «Найти вакансии»."
                        : "Нажмите «Найти вакансии», чтобы загрузить демонстрационные примеры."}
                  </p>
                  <button
                    className="button secondary"
                    onClick={() => {
                      setFilter(defaults);
                      setRequiredText("");
                    }}
                  >
                    Сбросить фильтры
                  </button>
                </div>
              ) : (
                <div
                  className={loading ? "vacancy-list updating" : "vacancy-list"}
                >
                  {items.slice(0, visible).map((row) => (
                    <VacancyCard
                      key={row.vacancy.id}
                      row={row}
                      coverLetter={() => setLetterVacancy(row.vacancy)}
                      editCompany={() => {
                        setNotice("");
                        setCompany({
                          name: row.vacancy.employer,
                          info: row.company ?? {
                            employerId: row.vacancy.employerId,
                            employees: null,
                            rating: null,
                            source: "",
                            fromWebsite: false,
                            reviews: null,
                          },
                        });
                      }}
                    />
                  ))}
                </div>
              )}
              {visible < items.length && (
                <button
                  className="button secondary load-more"
                  onClick={() => setVisible((v) => v + 30)}
                >
                  Показать ещё {Math.min(30, items.length - visible)}
                  <ChevronDown size={16} />
                </button>
              )}
              <footer className="results-footer">
                <span>
                  <span className="hh-mark">
                    {state?.mode === "demo" ? "D" : "hh"}
                  </span>
                  {state?.mode === "demo"
                    ? "Вымышленные примеры"
                    : "Данные hh.ru"}
                </span>
                <span>Проверка каждые 5 минут, пока запущен backend</span>
              </footer>
            </section>
          </div>
        </div>
      </main>

      {letterVacancy && <CoverLetterDialog vacancy={letterVacancy} hasProfile={!!hasProfile} close={() => setLetterVacancy(null)} openProfile={openProfile} />}
      {modal === "profile" && (
        <Modal
          title="Мои навыки"
          close={() => {
            setModal(null);
            setNotice("");
          }}
        >
          <form onSubmit={saveProfile}>
            <p className="modal-intro">
              Добавьте стек вручную или вставьте текст резюме. Он хранится
              локально и не отправляется на hh.ru.
            </p>
            <label htmlFor="profile-skills">Навыки через запятую</label>
            <textarea
              id="profile-skills"
              rows={3}
              placeholder="C#, .NET, ASP.NET, PostgreSQL, Docker, React"
              value={skills}
              onChange={(e) => setSkills(e.target.value)}
              maxLength={10000}
            />
            <label htmlFor="resume-text">
              Текст резюме <span className="optional">необязательно</span>
            </label>
            <textarea
              id="resume-text"
              rows={7}
              value={resume}
              onChange={(e) => setResume(e.target.value)}
              maxLength={30000}
              placeholder="Навыки будут выделены по словарю технологий. Укажите редкие технологии вручную выше."
            />
            <p className="hint">
              Оценка — доля найденных в вакансии навыков, которые есть у вас.
              Это предварительное сравнение, без оценки опыта и уровня
              seniority.
            </p>
            {notice && (
              <p role="alert" className="form-error">
                {notice}
              </p>
            )}
            <div className="modal-actions">
              <button
                type="button"
                className="button secondary"
                onClick={() => setModal(null)}
              >
                Отмена
              </button>
              <button className="button primary" disabled={busy}>
                Сохранить навыки
              </button>
            </div>
          </form>
        </Modal>
      )}
      {modal === "help" && (
        <Modal title="Как работает Stack Scout" close={() => setModal(null)}>
          <div className="help-content">
            <p>
              Backend загружает вакансии hh.ru при запуске и затем каждые 5
              минут после входа по почте и коду. Playwright читает видимую
              разметку страниц. По умолчанию — до 3 страниц; количество карточек
              определяет hh.ru. Новая загрузка целиком заменяет предыдущую.
            </p>
            <h3>Сначала стек, затем соответствие</h3>
            <p>
              Строгий режим принимает заголовки с C# или .NET. Для нейтрального
              заголовка нужны C# и .NET в кратком описании либо явные ключевые
              навыки. Заголовки с Java, Python, Go и другими основными языками
              исключаются, как и отдельные frontend-роли без fullstack или
              Blazor.
            </p>
            <p>
              Процент — покрытие найденных навыков вакансии вашим профилем.
              Краткое описание может быть неполным, поэтому 100% не гарантирует
              полное соответствие. Обязательные навыки работают по правилу
              «все».
            </p>
            <h3>Что с данными компаний?</h3>
            <p>
              Рейтинг и число отзывов читаются со страницы hh.ru, если они
              доступны. Тексты отзывов не загружаются. Численность и свою оценку
              можно добавить вручную с источником; ваши сведения имеют
              приоритет.
            </p>
            <p className="hint">
              Отклики и чаты сейчас не собираются. Если дата публикации не
              указана в разметке списка, показывается время проверки, а не
              выдуманная дата публикации.
            </p>
            <a
              href="https://hh.ru/"
              target="_blank"
              rel="noreferrer"
              className="external-link"
            >
              Открыть hh.ru
              <ArrowUpRight size={16} />
            </a>
          </div>
        </Modal>
      )}
      {company && (
        <Modal
          title={company.name}
          close={() => {
            setCompany(null);
            setNotice("");
          }}
        >
          <form onSubmit={saveCompany}>
            <p className="modal-intro">
              Ваши сведения имеют приоритет над рейтингом со страницы hh.ru.
              Тексты отзывов автоматически не загружаются.
            </p>
            <label htmlFor="company-size">Число сотрудников</label>
            <input
              id="company-size"
              type="number"
              min="0"
              max="10000000"
              value={company.info.employees ?? ""}
              onChange={(e) =>
                setCompany({
                  ...company,
                  info: {
                    ...company.info,
                    employees: numberOrNull(e.target.value),
                  },
                })
              }
              placeholder="Неизвестно"
            />
            <label htmlFor="company-rating">Моя оценка, от 0 до 5</label>
            <input
              id="company-rating"
              type="number"
              min="0"
              max="5"
              step="0.1"
              value={company.info.rating ?? ""}
              onChange={(e) =>
                setCompany({
                  ...company,
                  info: {
                    ...company.info,
                    rating: numberOrNull(e.target.value),
                  },
                })
              }
              placeholder="Неизвестно"
            />
            <label htmlFor="company-source">Источник или заметка</label>
            <input
              id="company-source"
              maxLength={500}
              value={company.info.source ?? ""}
              onChange={(e) =>
                setCompany({
                  ...company,
                  info: { ...company.info, source: e.target.value },
                })
              }
              placeholder="Например, сайт компании, октябрь 2026"
            />
            {notice && (
              <p role="alert" className="form-error">
                {notice}
              </p>
            )}
            <div className="modal-actions">
              <button className="button primary" disabled={busy}>
                Сохранить сведения
              </button>
            </div>
          </form>
        </Modal>
      )}
    </div>
  );
}

function VacancyCard({
  row,
  editCompany,
  coverLetter,
}: {
  row: Ranked;
  editCompany: () => void;
  coverLetter: () => void;
}) {
  const { vacancy: v, match, company } = row;
  const [expanded, setExpanded] = useState(false);
  const initials = v.employer
    .split(/[\s-]/)
    .filter(Boolean)
    .map((x) => x[0])
    .slice(0, 2)
    .join("")
    .toUpperCase();
  const color = ["sage", "sand", "blue", "lilac"][v.employer.length % 4];
  return (
    <article className="vacancy-card">
      <div className="card-top">
        <div className={`company-avatar ${color}`}>{initials || "К"}</div>
        <div className="company-title">
          <span>{v.employer || "Компания не указана"}</span>
          <small>
            {v.publicationDateKnown === false ? "Проверено " : ""}
            {new Date(v.publishedAt).toLocaleDateString("ru-RU", {
              day: "numeric",
              month: "short",
            })}
          </small>
        </div>
        <div
          className={`match-badge ${match.score == null ? "unknown" : match.score >= 70 ? "high" : "medium"}`}
          title="Доля найденных навыков вакансии, которые есть в вашем профиле"
        >
          <Crosshair size={14} />
          {match.score == null ? "Без оценки" : `${match.score}% совпадение`}
        </div>
      </div>
      <h3>
        <a href={v.url} target="_blank" rel="noreferrer">
          {v.title}
        </a>
      </h3>
      <div className="card-salary">
        {salary(v)}
        {v.salaryFrom != null || v.salaryTo != null ? (
          <span>
            {v.gross === true
              ? "до вычета налогов"
              : v.gross === false
                ? "на руки"
                : "налоги не уточнены"}
          </span>
        ) : null}
      </div>
      <div className="job-meta">
        <span>
          <MapPin size={14} />
          {v.area || "Регион не указан"}
        </span>
        {v.experience && (
          <span>
            <BriefcaseBusiness size={14} />
            {v.experience}
          </span>
        )}
        {v.remote && (
          <span className="remote-tag">
            <span />
            Удалённо
          </span>
        )}
      </div>
      {v.requirement && <p className="job-description">{v.requirement}</p>}
      <div className="skill-tags">
        {match.required.slice(0, expanded ? undefined : 7).map((skill) => (
          <span
            key={skill}
            className={match.matched.includes(skill) ? "matched" : ""}
          >
            {match.matched.includes(skill) && <Check size={12} />}
            {skill}
          </span>
        ))}
        {!expanded && match.required.length > 7 && (
          <span>+{match.required.length - 7}</span>
        )}
      </div>
      {expanded && (
        <div className="match-details">
          <strong>Почему эта вакансия в списке</strong>
          <p>
            {v.skillsVerified
              ? "Навыки из тегов карточки hh.ru."
              : "Навыки выделены из заголовка и краткого описания. Полные требования могут отличаться."}
          </p>
          <p>
            <b>Совпало:</b>{" "}
            {match.matched.join(", ") || "Нет данных для сравнения"}
          </p>
          <p>
            <b>Нет в вашем профиле:</b>{" "}
            {match.missing.join(", ") || "Среди найденных навыков — нет"}
          </p>
          {v.responsibility && (
            <p>
              <b>Задачи:</b> {v.responsibility}
            </p>
          )}
          <p>
            <b>Размер компании:</b>{" "}
            {company?.employees != null
              ? `${fmt(company.employees)} сотрудников`
              : "Неизвестен"}
            <br />
            <b>
              {company?.fromWebsite ? "Рейтинг hh.ru:" : "Моя оценка:"}
            </b>{" "}
            {company?.rating != null ? `${company.rating} / 5` : "Не указана"}
            {company?.fromWebsite && company.reviews != null && (
              <> · {fmt(company.reviews)} отзывов</>
            )}
            {company?.source && (
              <>
                <br />
                <b>Источник:</b> {company.source}
              </>
            )}
          </p>
          {v.employerId && (
            <button className="text-button" onClick={editCompany}>
              Добавить сведения о компании
            </button>
          )}
        </div>
      )}
      <div className="card-footer">
        <button className="explain-button letter-card-button" onClick={coverLetter}><FileText size={15} /> Сопроводительное</button>
        <button
          className="explain-button"
          aria-expanded={expanded}
          onClick={() => setExpanded(!expanded)}
        >
          {expanded ? "Скрыть детали" : "Разобрать совпадение"}
          <ChevronDown size={14} className={expanded ? "rotated" : ""} />
        </button>
        <a
          href={v.url}
          target="_blank"
          rel="noreferrer"
          className="open-vacancy"
        >
          Открыть на hh.ru
          <ArrowUpRight size={15} />
        </a>
      </div>
    </article>
  );
}
