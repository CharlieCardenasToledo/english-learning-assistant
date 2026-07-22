"use client";

import { Minus, Square, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { AudioWaveform } from "@/components/AudioWaveform/AudioWaveform";

interface TitleBarProps {
  sessionActive?: boolean;
  sessionDuration?: string;
  questionCount?: number;
}

async function getWindow() {
  const { getCurrentWindow } = await import("@tauri-apps/api/window");
  return getCurrentWindow();
}

function startDrag(e: React.MouseEvent) {
  if (e.buttons !== 1) return;
  // Doble clic → toggle maximize (patrón oficial Tauri 2)
  if (e.detail === 2) {
    getWindow().then(w => w.toggleMaximize());
  } else {
    getWindow().then(w => w.startDragging());
  }
}

export function TitleBar({ sessionActive, sessionDuration, questionCount }: TitleBarProps) {
  return (
    <div className="flex items-center justify-between h-9 px-3 bg-card/80 border-b border-border select-none">
      {/* Izquierda: arrastra la ventana */}
      <div
        className="flex items-center gap-2 flex-1 h-full cursor-default"
        onMouseDown={startDrag}
      >
        <div className={cn("w-2 h-2 rounded-full shrink-0", sessionActive ? "bg-green-400 animate-pulse" : "bg-muted-foreground")} />
        <span className="text-xs font-medium text-foreground">English Learning Assistant</span>
        <AudioWaveform active={!!sessionActive} />
        {sessionActive && sessionDuration && (
          <span className="text-xs text-muted-foreground font-mono">{sessionDuration}</span>
        )}
        {sessionActive && questionCount !== undefined && questionCount > 0 && (
          <span className="text-xs text-muted-foreground">· {questionCount} pregunta{questionCount !== 1 ? "s" : ""}</span>
        )}
      </div>

      {/* Derecha: controles de ventana — stopPropagation para no activar drag */}
      <div
        className="flex items-center gap-0.5"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <button
          onClick={() => getWindow().then(w => w.minimize())}
          aria-label="Minimizar ventana"
          className="p-1.5 rounded hover:bg-muted transition-colors text-muted-foreground hover:text-foreground"
        >
          <Minus size={12} />
        </button>
        <button
          onClick={() => getWindow().then(w => w.toggleMaximize())}
          aria-label="Maximizar ventana"
          className="p-1.5 rounded hover:bg-muted transition-colors text-muted-foreground hover:text-foreground"
        >
          <Square size={11} />
        </button>
        <button
          onClick={() => getWindow().then(w => w.close())}
          aria-label="Cerrar ventana"
          className="p-1.5 rounded hover:bg-destructive transition-colors text-muted-foreground hover:text-destructive-foreground"
        >
          <X size={12} />
        </button>
      </div>
    </div>
  );
}