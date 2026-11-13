import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  Check,
  ExternalLink,
  KeyRound,
  LogOut,
  Mail,
  RefreshCw,
} from "lucide-react";
import { api } from "./api";
import type { HhAuthState } from "./api";

export default function HhAuthPanel({
  auth,
  blocked,
  refresh,
  connected,
}: {
  auth: HhAuthState;
  blocked: boolean;
  refresh: () => Promise<void>;
  connected: () => Promise<void>;
}) {
  const [current, setCurrent] = useState(auth);
  const [email, setEmail] = useState("");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [restart, setRestart] = useState(false);
  const [now, setNow] = useState(Date.now());
  useEffect(() => setCurrent(auth), [auth]);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  const waiting = busy || blocked || current.busy;
  const retrySeconds = current.retryAt
    ? Math.max(0, Math.ceil((Date.parse(current.retryAt) - now) / 1000))
    : 0;
  const authenticated = current.stage === "authenticated";
  const pending = current.stage === "codeRequired" && !restart;

  async function act(action: string, body?: unknown) {
    setBusy(true);
    setError("");
    try {
      const result = await api<HhAuthState | undefined>(
        `hh-auth/${action}`,
        "POST",
        body,
      );
      if (result) setCurrent(result);
      if (action === "email") setRestart(false);
      if (action === "disconnect") {
        setEmail("");
        setCode("");
        setRestart(false);
      }
      await refresh();
      if (
        result?.stage === "authenticated" &&
        current.stage !== "authenticated"
      ) {
        setEmail("");
        setCode("");
        await connected();
      }
    } catch (e) {
      setError((e as Error).message);
      await refresh();
    } finally {
      setBusy(false);
      if (action === "code") setCode("");
    }
  }
  function requestCode(e: FormEvent) {
    e.preventDefault();
    void act("email", { email: email.trim() });
  }
  function submitCode(e: FormEvent) {
    e.preventDefault();
    void act("code", { code: code.trim() });
  }

  return (
    <section
      className={`auth-panel ${authenticated ? "authenticated" : ""}`}
      aria-label="Подключение hh.ru"
    >
      <div className="auth-heading">
        <span className="auth-symbol">
          {authenticated ? <Check size={20} /> : <Mail size={20} />}
        </span>
        <div>
          <h2>
            {authenticated ? "hh.ru подключён" : "Войдите в hh.ru по почте"}
          </h2>
          <p>
            {authenticated
              ? "Вакансии читаются из браузера вашей сессии."
              : "Получите письмо и введите код здесь. Пароль и токен API не нужны."}
          </p>
        </div>
        <span className="auth-step">
          {authenticated
            ? "СЕССИЯ АКТИВНА"
            : pending
              ? "ШАГ 2 ИЗ 2"
              : "ШАГ 1 ИЗ 2"}
        </span>
      </div>
      {!authenticated &&
        (current.stage !== "manualAction" || restart) &&
        (pending ? (
          <form className="auth-form" onSubmit={submitCode}>
            <div>
              <label htmlFor="hh-code">
                Код из письма{" "}
                {current.emailHint && <span>для {current.emailHint}</span>}
              </label>
              <input
                id="hh-code"
                type="text"
                inputMode="numeric"
                autoComplete="one-time-code"
                pattern="[0-9]{4,8}"
                minLength={4}
                maxLength={8}
                value={code}
                onChange={(e) => setCode(e.target.value.replace(/\D/g, ""))}
                placeholder="Введите код"
                required
                disabled={!!waiting}
              />
            </div>
            <button className="button primary" disabled={!!waiting}>
              <KeyRound size={16} />
              {busy ? "Проверяем…" : "Подтвердить вход"}
            </button>
          </form>
        ) : (
          <form className="auth-form" onSubmit={requestCode}>
            <div>
              <label htmlFor="hh-email">Почта аккаунта hh.ru</label>
              <input
                id="hh-email"
                type="email"
                autoComplete="email"
                maxLength={254}
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="you@example.com"
                required
                disabled={!!waiting}
              />
            </div>
            <button
              className="button primary"
              disabled={!!waiting || retrySeconds > 0}
            >
              <Mail size={16} />
              {busy
                ? "Запрашиваем…"
                : retrySeconds > 0
                  ? `Повтор через ${retrySeconds} с`
                  : "Получить код"}
            </button>
          </form>
        ))}
      {current.message && (
        <p
          className={`auth-message ${current.stage === "manualAction" ? "requires-action" : ""}`}
          role="status"
        >
          {current.message}
        </p>
      )}
      {error && (
        <p className="form-error" role="alert">
          {error}
        </p>
      )}
      <div className="auth-actions">
        {authenticated ? (
          <button
            className="text-button"
            disabled={!!waiting}
            onClick={() => void act("disconnect")}
          >
            <LogOut size={13} />
            Отключить и забыть вход
          </button>
        ) : (
          <button
            className="text-button"
            disabled={!!waiting}
            onClick={() => void act("check")}
          >
            <RefreshCw size={13} />
            {current.canRestore
              ? "Проверить сохранённый вход"
              : "Проверить вход"}
          </button>
        )}
        <button
          className="text-button"
          disabled={!!waiting}
          onClick={() => void act("show-browser")}
        >
          <ExternalLink size={13} />
          Открыть браузер hh.ru
        </button>
        {!authenticated && (pending || current.stage === "manualAction") && (
          <button
            className="text-button"
            disabled={!!waiting || retrySeconds > 0}
            onClick={() => {
              setRestart(true);
              setCode("");
              setError("");
            }}
          >
            Запросить новый код
            {retrySeconds > 0 ? ` через ${retrySeconds} с` : ""}
          </button>
        )}
      </div>
      <p className="auth-privacy">
        Вход выполняется на hh.ru в отдельном Chromium. Сессия сохраняется
        локально на этом компьютере; код не записывается в базу. Если hh.ru
        запросит проверку, выполните её в окне браузера и нажмите «Проверить
        вход».
      </p>
    </section>
  );
}
