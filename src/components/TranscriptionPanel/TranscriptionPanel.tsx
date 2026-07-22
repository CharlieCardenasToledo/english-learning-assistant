"use client";

import { useLayoutEffect, useRef } from "react";
import { motion, AnimatePresence } from "framer-motion";
import type { TranscriptionLine } from "@/types";

interface Props {
  lines: TranscriptionLine[];
  partial?: string;
}

export function TranscriptionPanel({ lines, partial }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);

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
  }, [lines, partial]);

  return (
    <div className="flex flex-col h-full overflow-hidden">
      <div className="px-3 py-1.5 border-b border-border bg-muted/30">
        <span className="text-xs font-semibold text-muted-foreground uppercase tracking-wide">Transcripción</span>
      </div>
      <div ref={scrollRef} className="relative flex-1 overflow-y-auto p-3 space-y-1 scrollbar-thin scrollbar-thumb-slate-300 scrollbar-track-transparent">

        {lines.length === 0 && !partial && <p className="py-10 text-center text-xs text-slate-400">Inicia una sesión para ver la transcripción.</p>}
        <AnimatePresence initial={false}>
          {lines.map((line) => (
            <motion.p
              key={line.sequenceId}
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.2 }}
              className="text-sm text-foreground leading-relaxed"
            >
              {line.text}
            </motion.p>
          ))}
        </AnimatePresence>
        {partial && (
          <p className="text-sm text-indigo-600/80 italic leading-relaxed border-l-2 border-indigo-300 pl-2">{partial}</p>
        )}


      </div>
    </div>
  );
}
