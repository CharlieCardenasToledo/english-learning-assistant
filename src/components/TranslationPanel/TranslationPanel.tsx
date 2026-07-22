"use client";

import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { motion, AnimatePresence } from "framer-motion";
import type { TranslationLine } from "@/types";
import { dotnetRequest } from "@/hooks/useTauriInvoke";

interface Props {
  lines: TranslationLine[];
}

interface Status {
  isServerRunning: boolean;
  isModelLoaded: boolean;
  provider?: string;
  modelName: string;
  error?: string;
}

export function TranslationPanel({ lines }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [status, setStatus] = useState<Status | null>(null);

  useLayoutEffect(() => {
    let secondFrame = 0;
    const firstFrame = requestAnimationFrame(() => {
      secondFrame = requestAnimationFrame(() => {
        const container = scrollRef.current;
        if (container) container.scrollTop = container.scrollHeight;
      });
    });
    return () => {
      cancelAnimationFrame(firstFrame);
      if (secondFrame) cancelAnimationFrame(secondFrame);
    };
  }, [lines]);

  useEffect(() => {
    let active = true;
    let checking = false;

    async function checkStatus() {
      if (checking) return;
      checking = true;
      try {
        const res = await dotnetRequest<Status>("settings", "getTranslationStatus");
        if (active) setStatus(res);
      } catch (err) {
        console.error("[Health] Check failed with error:", err);
        if (active) setStatus({ isServerRunning: false, isModelLoaded: false, provider: "", modelName: "", error: String(err) });
      } finally {
        checking = false;
      }
    }

    checkStatus();
    const interval = setInterval(checkStatus, 5000);
    return () => {
      active = false;
      clearInterval(interval);
    };
  }, []);

  const isServerRunning = status?.isServerRunning ?? false;
  const isModelLoaded = status?.isModelLoaded ?? false;
  const providerName = status?.provider ?? "";
  const modelName = status?.modelName ?? "";
  const errorMsg = status?.error ?? "";

  return (
    <div className="flex flex-col h-full overflow-hidden">
      <div className="px-3 py-1.5 border-b border-border bg-muted/30 flex items-center justify-between">
        <span className="text-xs font-semibold text-muted-foreground uppercase tracking-wide">Traducción</span>
        {status && (
          <div
            className="flex items-center gap-1.5 px-2 py-0.5 rounded-full border bg-white/50 text-[10px] text-gray-500 font-medium max-w-[260px] truncate"
            title={isServerRunning ? `Proveedor: ${providerName}\nModelo: ${modelName}` : `${providerName || "Proveedor"} desconectado${errorMsg ? `: ${errorMsg}` : ""}`}
          >
            <span
              className={`w-1.5 h-1.5 rounded-full shrink-0 ${
                isServerRunning && isModelLoaded
                  ? "bg-green-500 animate-pulse"
                  : isServerRunning
                  ? "bg-amber-500"
                  : "bg-red-500"
              }`}
            />
            <span className="truncate">
              {isServerRunning
                ? (isModelLoaded
                  ? (providerName || "Proveedor") + " · " + (modelName || "Modelo activo")
                  : (providerName || "Proveedor") + " · Cargando modelo…")
                : (providerName || "Proveedor") + " · Offline"}
            </span>
          </div>
        )}
      </div>
      <div ref={scrollRef} className="relative flex-1 overflow-y-auto p-3 space-y-1 scrollbar-thin scrollbar-thumb-border">

        {lines.length === 0 && <p className="py-10 text-center text-xs text-slate-400">Las traducciones aparecerán aquí cuando el proveedor esté listo.</p>}
        <AnimatePresence initial={false}>
          {lines.map((line, i) => (
            <motion.p
              key={i}
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.2 }}
              className="text-sm text-foreground leading-relaxed"
            >
              {line.translated}
            </motion.p>
          ))}
        </AnimatePresence>


      </div>
    </div>
  );
}