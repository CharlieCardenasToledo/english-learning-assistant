"use client";

import { useState, useEffect } from "react";
import { ChevronLeft, ChevronRight, MessageSquare, Clock, Plus } from "lucide-react";
import { cn } from "@/lib/utils";
import { dotnetRequest } from "@/hooks/useTauriInvoke";

// ─── Types ────────────────────────────────────────────────────────────────────

interface Session {
  id: number;
  title: string;
  startTime: string;
  questionCount?: number;
}

// ─── Helpers ─────────────────────────────────────────────────────────────────

function relativeDate(iso: string): string {
  try {
    const d = new Date(iso);
    const now = new Date();
    const diffMs = now.getTime() - d.getTime();
    const diffDays = Math.floor(diffMs / 86_400_000);
    if (diffDays === 0) return "Hoy";
    if (diffDays === 1) return "Ayer";
    if (diffDays < 7) return `Hace ${diffDays} días`;
    return d.toLocaleDateString("es-ES", { day: "numeric", month: "short" });
  } catch {
    return "";
  }
}

// ─── Main ─────────────────────────────────────────────────────────────────────

interface Props {
  onNewSession?: () => void;
  onSelectSession?: (id: number) => void;
  activeSessionId?: number | null;
}

export function Sidebar({ onNewSession, onSelectSession, activeSessionId }: Props) {
  const [expanded, setExpanded]   = useState(false);
  const [sessions, setSessions]   = useState<Session[]>([]);
  const [loading, setLoading]     = useState(false);

  useEffect(() => {
    if (!expanded) return;
    setLoading(true);
    dotnetRequest("session", "initialize")
      .catch(() => {/* ya inicializado */})
      .then(() => dotnetRequest<Session[]>("session", "list"))
      .then((data) => setSessions(Array.isArray(data) ? data : []))
      .catch(() => setSessions([]))
      .finally(() => setLoading(false));
  }, [expanded]);

  return (
    <div
      className={cn(
        "h-full shrink-0 transition-all duration-200 ease-in-out z-40",
        expanded ? "w-56" : "w-12"
      )}
    >
      {/* Panel */}
      <div className="flex flex-col h-full w-full bg-white border-r border-gray-200 overflow-hidden">

        {/* Toggle button */}
        <div className="flex items-center justify-between h-9 px-2 border-b border-gray-100 shrink-0">
          {expanded && (
            <span className="text-xs font-semibold text-gray-500 uppercase tracking-wider px-1">
              Historial
            </span>
          )}
          <button
            onClick={() => setExpanded((v) => !v)}
            onMouseDown={(e) => e.stopPropagation()}
            className="ml-auto p-1 rounded hover:bg-gray-100 transition-colors text-gray-400 hover:text-gray-700"
            title={expanded ? "Colapsar" : "Expandir"}
          >
            {expanded ? <ChevronLeft size={14} /> : <ChevronRight size={14} />}
          </button>
        </div>

        {/* New session button */}
        <button
          onClick={onNewSession}
          className={cn(
            "flex items-center gap-2 mx-2 mt-2 px-2 py-1.5 rounded-lg text-xs font-medium",
            "bg-gray-900 text-white hover:bg-gray-700 transition-colors shrink-0",
            !expanded && "justify-center"
          )}
          title="Nueva sesión"
        >
          <Plus size={13} />
          {expanded && "Nueva sesión"}
        </button>

        {/* Session list */}
        <div className="flex-1 overflow-y-auto mt-2 space-y-0.5 px-1">
          {loading && expanded && (
            <p className="text-xs text-gray-400 text-center mt-4">Cargando…</p>
          )}
          {!loading && expanded && sessions.length === 0 && (
            <p className="text-xs text-gray-400 text-center mt-4">Sin sesiones previas</p>
          )}
          {sessions.map((s) => {
            const isActive = s.id === activeSessionId;
            return (
              <button
                key={s.id}
                onClick={() => onSelectSession?.(s.id)}
                className={cn(
                  "w-full flex items-start gap-2 px-2 py-2 rounded-lg text-left transition-colors",
                  isActive
                    ? "bg-gray-100 text-gray-900"
                    : "text-gray-600 hover:bg-gray-50 hover:text-gray-900",
                  !expanded && "justify-center"
                )}
                title={expanded ? undefined : s.title}
              >
                <MessageSquare size={13} className="mt-0.5 shrink-0 text-gray-400" />
                {expanded && (
                  <div className="min-w-0 flex-1">
                    <p className="text-xs font-medium truncate">{s.title || "Sesión sin título"}</p>
                    <div className="flex items-center gap-1 mt-0.5">
                      <Clock size={9} className="text-gray-400" />
                      <span className="text-[10px] text-gray-400">{relativeDate(s.startTime)}</span>
                      {s.questionCount !== undefined && s.questionCount > 0 && (
                        <span className="text-[10px] text-gray-400">· {s.questionCount} p</span>
                      )}
                    </div>
                  </div>
                )}
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
