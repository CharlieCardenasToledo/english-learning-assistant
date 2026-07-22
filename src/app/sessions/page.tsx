"use client";

import { useState, useEffect, Suspense } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Sidebar } from "@/components/Sidebar/Sidebar";
import { dotnetRequest } from "@/hooks/useTauriInvoke";
import { toast } from "sonner";
import { Download, Trash2, ExternalLink, Calendar, MessageSquare } from "lucide-react";
import { cn } from "@/lib/utils";
import type { Session, SessionDetail } from "@/types";

function relativeDate(iso: string): string {
  try {
    const d = new Date(iso);
    const now = new Date();
    const diffMs = now.getTime() - d.getTime();
    const diffDays = Math.floor(diffMs / 86_400_000);
    if (diffDays === 0) return "Hoy";
    if (diffDays === 1) return "Ayer";
    if (diffDays < 7) return `Hace ${diffDays} días`;
    return d.toLocaleDateString("es-ES", { day: "numeric", month: "long", year: "numeric" });
  } catch {
    return "";
  }
}

function formatDuration(start: string, end?: string): string {
  try {
    const s = new Date(start);
    const e = end ? new Date(end) : new Date();
    const diff = Math.floor((e.getTime() - s.getTime()) / 1000);
    const h = Math.floor(diff / 3600);
    const m = Math.floor((diff % 3600) / 60);
    const sec = diff % 60;
    if (h > 0) return `${h}h ${m}m`;
    if (m > 0) return `${m}m ${sec}s`;
    return `${sec}s`;
  } catch {
    return "";
  }
}

function SessionsContent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [detail, setDetail] = useState<SessionDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [selectedId, setSelectedId] = useState<number | null>(
    searchParams.get("id") ? Number(searchParams.get("id")) : null
  );

  useEffect(() => {
    loadSessions();
  }, []);

  useEffect(() => {
    if (!selectedId) {
      setDetail(null);
      return;
    }
    setDetailLoading(true);
    dotnetRequest<SessionDetail>("session", "get", { id: selectedId })
      .then(setDetail)
      .catch(() => toast.error("No se pudo cargar el detalle de la sesión"))
      .finally(() => setDetailLoading(false));
  }, [selectedId]);

  async function loadSessions() {
    setLoading(true);
    try {
      await dotnetRequest("session", "initialize");
      const data = await dotnetRequest<Session[]>("session", "list");
      setSessions(Array.isArray(data) ? data : []);
    } catch {
      toast.error("No se pudo cargar el historial");
    } finally {
      setLoading(false);
    }
  }

  async function handleExport(id: number) {
    try {
      const result = await dotnetRequest<{ markdown: string }>("session", "export", { id });
      const blob = new Blob([result.markdown], { type: "text/markdown" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `session-${id}.md`;
      a.click();
      URL.revokeObjectURL(url);
      toast.success("Sesión exportada");
    } catch {
      toast.error("Error al exportar la sesión");
    }
  }

  async function handleDelete(id: number) {
    try {
      await dotnetRequest("session", "delete", { id });
      setSessions((prev) => prev.filter((s) => s.id !== id));
      if (selectedId === id) setSelectedId(null);
      toast.success("Sesión eliminada");
    } catch {
      toast.error("Error al eliminar la sesión");
    }
  }

  function handleOpen(id: number) {
    setSelectedId(id);
    router.push("/sessions?id=" + id);
  }

  return (
    <div className="flex h-screen bg-gray-50 overflow-hidden">
      <Sidebar
        activeSessionId={selectedId}
        onSelectSession={setSelectedId}
        onNewSession={() => router.push("/")}
      />

      <main className="flex-1 overflow-y-auto p-6">
        <div className="max-w-3xl mx-auto">
          <div className="flex items-center justify-between mb-6">
            <div>
              <h1 className="text-xl font-semibold text-gray-900">Historial de sesiones</h1>
              <p className="text-sm text-gray-500 mt-0.5">{sessions.length} sesiones registradas</p>
            </div>
          </div>

          {loading && (
            <div className="flex items-center justify-center h-40">
              <span className="text-sm text-gray-400">Cargando…</span>
            </div>
          )}

          {!loading && sessions.length === 0 && (
            <div className="flex flex-col items-center justify-center h-40 gap-2 text-gray-400">
              <MessageSquare size={32} strokeWidth={1.5} />
              <p className="text-sm">Sin sesiones previas. ¡Inicia una nueva!</p>
            </div>
          )}

          <div className="space-y-3">
            {sessions.map((s) => {
              const isSelected = s.id === selectedId;
              return (
                <div
                  key={s.id}
                  onClick={() => setSelectedId(s.id)}
                  className={cn(
                    "rounded-xl border p-4 cursor-pointer transition-all",
                    isSelected
                      ? "border-gray-900 bg-white shadow-sm"
                      : "border-gray-200 bg-white hover:border-gray-300 hover:shadow-sm"
                  )}
                >
                  <div className="flex items-start justify-between gap-4">
                    <div className="flex-1 min-w-0">
                      <p className="text-sm font-medium text-gray-900 truncate">
                        {(s as any).title || `Sesión #${s.id}`}
                      </p>
                      <div className="flex items-center gap-3 mt-1.5">
                        <span className="flex items-center gap-1 text-xs text-gray-500">
                          <Calendar size={11} />
                          {relativeDate(s.startTime)}
                        </span>
                        {s.endTime && (
                          <span className="text-xs text-gray-400">
                            {formatDuration(s.startTime, s.endTime)}
                          </span>
                        )}
                        {s.questionCount > 0 && (
                          <span className="flex items-center gap-1 text-xs text-gray-500">
                            <MessageSquare size={11} />
                            {s.questionCount} {s.questionCount === 1 ? "pregunta" : "preguntas"}
                          </span>
                        )}
                        <span className="text-xs text-gray-400">{s.transcriptionCount} seg.</span>
                      </div>
                    </div>

                    <div className="flex items-center gap-1 shrink-0">
                      <button
                        onClick={(e) => { e.stopPropagation(); handleOpen(s.id); }}
                        className="p-1.5 rounded-lg text-gray-400 hover:text-gray-700 hover:bg-gray-100 transition-colors"
                        title="Abrir sesión"
                      >
                        <ExternalLink size={14} />
                      </button>
                      <button
                        onClick={(e) => { e.stopPropagation(); handleExport(s.id); }}
                        className="p-1.5 rounded-lg text-gray-400 hover:text-gray-700 hover:bg-gray-100 transition-colors"
                        title="Exportar a Markdown"
                      >
                        <Download size={14} />
                      </button>
                      <button
                        onClick={(e) => { e.stopPropagation(); handleDelete(s.id); }}
                        className="p-1.5 rounded-lg text-gray-400 hover:text-red-500 hover:bg-red-50 transition-colors"
                        title="Eliminar sesión"
                      >
                        <Trash2 size={14} />
                      </button>
                    </div>
                  </div>
                </div>
              );
            })}
          {selectedId && (
            <section className="mt-6 rounded-xl border border-gray-200 bg-white p-4 shadow-sm" aria-label="Detalle de sesión">
              {detailLoading && <p className="text-sm text-gray-400">Cargando detalle…</p>}
              {!detailLoading && detail && (
                <>
                  <div className="mb-4 flex items-start justify-between gap-3">
                    <div>
                      <h2 className="text-base font-semibold text-gray-900">{detail.title || "Sesión #" + detail.id}</h2>
                      <p className="mt-1 text-xs text-gray-500">{detail.entries.length} segmentos · {detail.questions.length} preguntas</p>
                    </div>
                    <button onClick={() => setSelectedId(null)} className="text-xs text-gray-500 hover:text-gray-900">Cerrar detalle</button>
                  </div>
                  <div className="max-h-96 space-y-3 overflow-y-auto border-t border-gray-100 pt-3">
                    {detail.entries.length === 0 && <p className="text-sm text-gray-400">Esta sesión no contiene transcripción.</p>}
                    {detail.entries.map((entry) => (
                      <article key={entry.id} className="border-b border-gray-100 pb-3 last:border-0">
                        <p className="text-sm text-gray-800">{entry.originalText}</p>
                        {entry.translatedText && <p className="mt-1 text-sm text-indigo-600">{entry.translatedText}</p>}
                        {entry.aiResponse && <p className="mt-2 rounded-lg bg-violet-50 p-2 text-sm text-violet-800"><strong>Tutor IA:</strong> {entry.aiResponse}</p>}
                      </article>
                    ))}
                  </div>
                </>
              )}
            </section>
          )}
          </div>
        </div>
      </main>
    </div>
  );
}

export default function SessionsPage() {
  return (
    <Suspense fallback={null}>
      <SessionsContent />
    </Suspense>
  );
}