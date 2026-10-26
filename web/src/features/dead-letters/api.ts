import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { api } from "../../shared/api/client";
import type { DeadLetter } from "../../shared/api/types";
import { refresh } from "../../shared/theme/tokens";

export const useDeadLetters = () =>
  useQuery({
    queryKey: ["dead-letters"],
    queryFn: () => api.get<DeadLetter[]>("/v1/dead-letters"),
    refetchInterval: refresh.list,
  });

export function useReplay() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (d: DeadLetter) => api.post<void>(`/v1/dead-letters/${d.partition}/${d.offset}/replay`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["dead-letters"] });
      void qc.invalidateQueries({ queryKey: ["overview"] });
    },
  });
}
