"use client";

import { Mic, Square, Settings, Download, Loader2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { dotnetRequest } from "@/hooks/useTauriInvoke";
import { toast } from "sonner";

interface Props {
  isActive: boolean;
  isStarting?: boolean;
  barHeights: string[];
  onStart: () => void;
  onStop: () => void;
  onSettings: () => void;
  currentSessionId?: number;
}

export function SessionControls({ isActive, isStarting = false, barHeights, onStart, onStop, onSettings, currentSessionId }: Props) {
  async function handleExport() {
    if (!currentSessionId) return;
    try {
      const res = await dotnetRequest<{ markdown: string }>("session", "export", { id: currentSessionId });
      const blob = new Blob([res.markdown], { type: "text/markdown" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `session-${currentSessionId}.md`;
      a.click();
      URL.revokeObjectURL(url);
      toast.success("Sesión exportada.");
    } catch {
      toast.error("Error al exportar la sesión.");
    }
  }

  return (
    <div className="fixed bottom-6 left-0 right-0 z-30 flex justify-center pointer-events-none">
      <div className="bg-white/90 backdrop-blur-xl border border-slate-200/80 rounded-full shadow-2xl shadow-slate-400/30 flex items-center px-4 py-2 gap-1 pointer-events-auto">
        {/* Settings */}
        <button
          onClick={onSettings}
          className="w-8 h-8 flex items-center justify-center text-gray-400 hover:text-gray-600 rounded-full hover:bg-gray-100 transition-colors"
          title="Configuración"
        >
          <Settings size={15} />
        </button>

        {/* Mic / Stop button */}
        <button
          onClick={isActive ? onStop : onStart}
          disabled={isStarting}
          className={cn(
            "w-12 h-12 flex items-center justify-center rounded-full text-white transition-colors",
            isActive ? "bg-red-500 hover:bg-red-600" : "bg-red-500 hover:bg-red-600 disabled:cursor-wait disabled:opacity-70"
          )}
          title={isStarting ? "Preparando sesión" : isActive ? "Detener" : "Iniciar"}
          aria-label={isStarting ? "Preparando sesión" : isActive ? "Detener sesión" : "Iniciar sesión"}
        >
          {isStarting ? <Loader2 size={18} className="animate-spin" /> : isActive ? <Square size={16} /> : <Mic size={20} />}
        </button>

        {/* Audio waveform bars */}
        <div className="flex items-center space-x-1 mx-3">
          {barHeights.map((height, i) => (
            <div
              key={i}
              className={cn(
                "w-1 rounded-full transition-all duration-200 ease-out",
                isActive ? "bg-red-500 animate-pulse" : "bg-gray-200"
              )}
              style={{ height: isActive ? height : "4px" }}
            />
          ))}
        </div>

        {/* Export (only when session exists) */}
        {currentSessionId && (
          <button
            onClick={handleExport}
            className="w-8 h-8 flex items-center justify-center text-gray-400 hover:text-gray-600 rounded-full hover:bg-gray-100 transition-colors"
            title="Exportar"
          >
            <Download size={15} />
          </button>
        )}
      </div>
    </div>
  );
}