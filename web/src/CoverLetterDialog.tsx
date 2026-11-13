import { useEffect, useRef, useState } from "react";
import { Copy, FileText, RefreshCw, Sparkles, X } from "lucide-react";
import { api } from "./api";
import type { Vacancy } from "./api";

type Letter = { id: string; text: string; model: string; createdAt: string; updatedAt: string; contextChanged: boolean };
type Details = { description: string; fetchedAt: string; hash: string };
type Context = { vacancy: Vacancy; details: Details | null; letters: Letter[] };
type Provider = { installed: boolean; chatGptLogin: boolean; message: string; models: string[] };

export default function CoverLetterDialog({ vacancy, hasProfile, close, openProfile }: {
  vacancy: Vacancy; hasProfile: boolean; close: () => void; openProfile: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const [context, setContext] = useState<Context | null>(null);
  const [provider, setProvider] = useState<Provider | null>(null);
  const [letter, setLetter] = useState<Letter | null>(null);
  const [text, setText] = useState("");
  const [model, setModel] = useState("auto");
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const dirty = letter !== null && text !== letter.text;
  const choose = (item: Letter | null) => { setLetter(item); setText(item?.text ?? ""); setNotice(""); };
  useEffect(() => {
    ref.current!.showModal();
    const controller = new AbortController();
    // Both GETs are read-only. There is deliberately no generation in this effect.
    Promise.all([
      api<Context>(`vacancies/${vacancy.id}/context`, "GET", undefined, controller.signal),
      api<Provider>("cover-letters/provider", "GET", undefined, controller.signal),
    ]).then(([data, status]) => {
      setContext(data); setProvider(status); choose(data.letters[0] ?? null);
    }).catch((e: Error) => { if (e.name !== "AbortError") setError(e.message); });
    return () => { controller.abort(); ref.current?.close(); };
  }, [vacancy.id]);

  const refresh = async () => {
    const data = await api<Context>(`vacancies/${vacancy.id}/context`);
    setContext(data); return data;
  };
  const run = async (action: string, work: () => Promise<void>) => {
    if (busy) return;
    setBusy(action); setError(""); setNotice("");
    try { await work(); } catch (e) { setError((e as Error).message); }
    finally { setBusy(""); }
  };
  const save = () => run("save", async () => {
    const saved = await api<Letter>(`cover-letters/${letter!.id}`, "PUT", { text });
    choose(saved); await refresh(); setNotice("Изменения сохранены в локальной базе.");
  });
  const generate = () => run("generate", async () => {
    const generated = await api<Letter>(`vacancies/${vacancy.id}/cover-letter`, "POST", { model, regenerate: !!letter });
    choose(generated); await refresh(); setNotice("Черновик готов. Проверьте факты перед использованием.");
  });
  const dismiss = () => { if (!busy && !dirty) close(); };

  return <dialog ref={ref} className="letter-dialog" onCancel={(e) => { e.preventDefault(); dismiss(); }}>
    <div className="modal-heading">
      <h2><FileText size={22} /> Сопроводительное письмо</h2>
      <button className="icon-button" onClick={dismiss} disabled={!!busy || dirty} aria-label="Закрыть"><X size={20} /></button>
    </div>
    <p className="letter-vacancy"><strong>{vacancy.title}</strong><br />{vacancy.employer}</p>
    <p className="modal-intro">Генерация запускается только кнопкой ниже через ваш Codex с входом в ChatGPT и расходует его лимиты. В модель передаются описание вакансии и сохранённые навыки/резюме. Письмо остаётся черновиком для вас.</p>
    {!hasProfile && <div className="letter-warning">Добавьте навыки или текст резюме, чтобы письмо опиралось на ваш опыт. <button className="text-button" onClick={() => { close(); openProfile(); }}>Открыть мой профиль</button></div>}
    {provider && <p className={provider.chatGptLogin ? "letter-status" : "letter-warning"}>{provider.message}</p>}
    {error && <div className="letter-error" role="alert">{error}</div>}
    {notice && <p className="letter-status" role="status">{notice}</p>}
    {context && <>
      <details className="letter-context">
        <summary>Контекст вакансии · {context.details ? `${context.details.description.length.toLocaleString("ru-RU")} символов сохранено` : "полный текст ещё не загружен"}</summary>
        <p>Полное описание загружается при генерации или кнопкой ниже и сохраняется в SQLite вместе с датой. Оно остаётся доступно после обновления выдачи.</p>
        {context.details && <><small>Получено {new Date(context.details.fetchedAt).toLocaleString("ru-RU")}</small><div className="letter-description">{context.details.description}</div>{context.details.description.length > 60000 && <p>Для модели используются первые 60 000 символов описания.</p>}</>}
        <button className="button secondary" disabled={!!busy || dirty} onClick={() => run("description", async () => {
          await api(`vacancies/${vacancy.id}/description`, "POST"); await refresh();
        })}><RefreshCw size={15} />{busy === "description" ? "Загружаем описание…" : context.details ? "Обновить описание с hh.ru" : "Сохранить полное описание"}</button>
      </details>
      <label htmlFor="letter-model">Модель ChatGPT / Codex</label>
      <select id="letter-model" value={model} onChange={(e) => setModel(e.target.value)} disabled={!!busy}>
        <option value="auto">Автоматически · Sol, затем Terra при недоступности</option>
        <option value="gpt-6.1-sol">GPT-6.1 Sol</option>
        <option value="gpt-6.1-terra">GPT-6.1 Terra</option>
      </select>
      {context.letters.length > 0 && <><label htmlFor="letter-version">Сохранённая версия</label><select id="letter-version" value={letter?.id ?? ""} disabled={!!busy || dirty} onChange={(e) => choose(context.letters.find((x) => x.id === e.target.value) ?? null)}>
        {context.letters.map((item, i) => <option key={item.id} value={item.id}>Версия {context.letters.length - i} · {new Date(item.createdAt).toLocaleString("ru-RU")} · {item.model}</option>)}
      </select></>}
      {letter?.contextChanged && <p className="letter-warning">Резюме или описание изменились после создания этой версии. Можно создать новое письмо по текущим данным.</p>}
      {letter ? <><label htmlFor="letter-text">Текст письма · можно редактировать</label>
        <textarea id="letter-text" className="letter-text" value={text} maxLength={12000} disabled={!!busy} onChange={(e) => setText(e.target.value)} />
        <small>{text.length.toLocaleString("ru-RU")} / 12 000 символов · сохранено {new Date(letter.updatedAt).toLocaleString("ru-RU")}</small>
      </> : <div className="letter-empty"><Sparkles size={25} /><p>Письмо ещё не создавалось.<br />Модель запустится после вашего нажатия.</p></div>}
      {dirty && <p className="letter-warning">Есть несохранённые изменения. Сохраните их или отмените перед закрытием и новой генерацией.</p>}
      <div className="modal-actions letter-actions">
        {letter && <>
          <button className="button secondary" disabled={!!busy || !text.trim()} onClick={() => run("copy", async () => { await navigator.clipboard.writeText(text); setNotice("Текст скопирован."); })}><Copy size={15} />Копировать</button>
          {dirty && <button className="text-button" disabled={!!busy} onClick={() => choose(letter)}>Отменить правки</button>}
          <button className="button secondary" disabled={!!busy || !dirty || !text.trim()} onClick={save}>{busy === "save" ? "Сохраняем…" : "Сохранить правки"}</button>
        </>}
        <button className="button primary" disabled={!!busy || dirty || !hasProfile || !provider?.chatGptLogin} onClick={generate}><Sparkles size={16} />{busy === "generate" ? "Codex готовит письмо…" : letter ? "Создать новую версию" : "Сгенерировать письмо"}</button>
      </div>
      {busy === "generate" && <p role="status" className="letter-status">Получаем полный текст и ждём ответ модели. Обычно это занимает до нескольких минут. Не закрывайте приложение.</p>}
    </>}
  </dialog>;
}
