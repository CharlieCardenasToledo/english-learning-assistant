"use client";

import { useEffect, useRef } from "react";

const BARS = 12;

export function AudioWaveform({ active }: { active: boolean }) {
  const barsRef = useRef<(HTMLSpanElement | null)[]>([]);
  const frameRef = useRef<number>();
  const lastEventRef = useRef<number>(0);
  const targets = useRef<number[]>(Array(BARS).fill(2));
  const current = useRef<number[]>(Array(BARS).fill(2));

  useEffect(() => {
    if (!active) {
      if (frameRef.current) cancelAnimationFrame(frameRef.current);
      barsRef.current.forEach(el => { if (el) el.style.height = "2px"; });
      return;
    }

    lastEventRef.current = 0;
    let unlisten: (() => void) | undefined;

    import("@tauri-apps/api/event").then(({ listen }) => {
      listen<unknown>("transcription-partial", () => {
        lastEventRef.current = Date.now();
      }).then(fn => { unlisten = fn; });
    });

    function tick() {
      frameRef.current = requestAnimationFrame(tick);
      const age = Date.now() - lastEventRef.current;
      // Intensidad 1.0 durante 400ms tras el evento, luego baja en 800ms
      const intensity = lastEventRef.current === 0
        ? 0
        : age < 400
          ? 1.0
          : Math.max(0, 1 - (age - 400) / 800);

      for (let i = 0; i < BARS; i++) {
        if (Math.random() < 0.12) {
          targets.current[i] = 2 + Math.random() * 9 * intensity;
        }
        current.current[i] += (targets.current[i] - current.current[i]) * 0.18;
        const el = barsRef.current[i];
        if (el) el.style.height = `${current.current[i].toFixed(1)}px`;
      }
    }

    frameRef.current = requestAnimationFrame(tick);

    return () => {
      if (frameRef.current) cancelAnimationFrame(frameRef.current);
      unlisten?.();
    };
  }, [active]);

  if (!active) return null;

  return (
    <div className="flex items-end gap-[2px] h-4 pb-0.5" aria-label="Actividad de audio">
      {Array.from({ length: BARS }, (_, i) => (
        <span
          key={i}
          ref={el => { barsRef.current[i] = el; }}
          className="inline-block w-[2px] rounded-full bg-green-400"
          style={{ height: "2px" }}
        />
      ))}
    </div>
  );
}
