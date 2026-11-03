/**
 * Background Workers & Agentic Task Queue
 * Asynchronous job queue with cancellation tokens and concurrency limiting.
 */

import { AgentState, OutboundEvent } from '../types/protocol.ts';

export interface TaskJob {
  taskId: string;
  prompt: string;
  workingDirectory?: string;
  environment?: Record<string, string>;
  execute: (context: TaskExecutionContext) => Promise<any>;
}

export interface TaskExecutionContext {
  taskId: string;
  abortSignal: AbortSignal;
  emit: (event: OutboundEvent) => void;
  setState: (state: AgentState, message?: string) => void;
  streamText: (text: string) => void;
}

export class TaskQueue {
  private queue: TaskJob[] = [];
  private activeJobs = new Map<string, {
    job: TaskJob;
    abortController: AbortController;
    startTime: number;
  }>();

  private concurrency: number;
  private eventEmitter: (event: OutboundEvent) => void;

  constructor(concurrency = 4, eventEmitter: (event: OutboundEvent) => void) {
    this.concurrency = concurrency;
    this.eventEmitter = eventEmitter;
  }

  public enqueue(job: TaskJob): void {
    this.queue.push(job);
    this.processNext();
  }

  public abort(taskId: string, reason = 'Aborted by user'): boolean {
    const active = this.activeJobs.get(taskId);
    if (active) {
      active.abortController.abort(reason);
      this.eventEmitter({
        event: 'agent_state',
        taskId,
        state: 'aborted',
        message: reason,
        timestamp: new Date().toISOString()
      });
      this.eventEmitter({
        event: 'task_completed',
        taskId,
        summary: {
          success: false,
          durationMs: Math.round(performance.now() - active.startTime),
          stepsCount: 0,
          error: reason,
          completedAt: new Date().toISOString()
        },
        timestamp: new Date().toISOString()
      });
      this.activeJobs.delete(taskId);
      this.processNext();
      return true;
    }

    const queueIdx = this.queue.findIndex(j => j.taskId === taskId);
    if (queueIdx >= 0) {
      this.queue.splice(queueIdx, 1);
      return true;
    }

    return false;
  }

  public isBusy(): boolean {
    return this.activeJobs.size > 0 || this.queue.length > 0;
  }

  public getActiveCount(): number {
    return this.activeJobs.size;
  }

  private async processNext(): Promise<void> {
    if (this.activeJobs.size >= this.concurrency || this.queue.length === 0) {
      return;
    }

    const job = this.queue.shift();
    if (!job) return;

    const abortController = new AbortController();
    const startTime = performance.now();
    let streamIndex = 0;

    this.activeJobs.set(job.taskId, {
      job,
      abortController,
      startTime
    });

    const context: TaskExecutionContext = {
      taskId: job.taskId,
      abortSignal: abortController.signal,
      emit: this.eventEmitter,
      setState: (state: AgentState, message?: string) => {
        this.eventEmitter({
          event: 'agent_state',
          taskId: job.taskId,
          state,
          message,
          timestamp: new Date().toISOString()
        });
      },
      streamText: (text: string) => {
        this.eventEmitter({
          event: 'stream_delta',
          taskId: job.taskId,
          text,
          index: streamIndex++,
          timestamp: new Date().toISOString()
        });
      }
    };

    context.setState('thinking', 'Processing task prompt');

    try {
      const result = await job.execute(context);

      if (!abortController.signal.aborted) {
        context.setState('completed');
        this.eventEmitter({
          event: 'task_completed',
          taskId: job.taskId,
          summary: {
            success: true,
            durationMs: Math.round(performance.now() - startTime),
            stepsCount: streamIndex,
            completedAt: new Date().toISOString()
          },
          timestamp: new Date().toISOString()
        });
      }
    } catch (err: any) {
      if (!abortController.signal.aborted) {
        context.setState('error', err?.message || String(err));
        this.eventEmitter({
          event: 'task_completed',
          taskId: job.taskId,
          summary: {
            success: false,
            durationMs: Math.round(performance.now() - startTime),
            stepsCount: streamIndex,
            error: err?.message || String(err),
            completedAt: new Date().toISOString()
          },
          timestamp: new Date().toISOString()
        });
      }
    } finally {
      this.activeJobs.delete(job.taskId);
      this.processNext();
    }
  }

  public cancelAll(): void {
    for (const [id, active] of this.activeJobs) {
      active.abortController.abort('Sidecar shutting down');
    }
    this.activeJobs.clear();
    this.queue = [];
  }
}
