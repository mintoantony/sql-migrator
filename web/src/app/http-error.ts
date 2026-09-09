import { HttpErrorResponse } from '@angular/common/http';

/** Turns any rejection from an HttpClient call into a message that is safe to show the user. */
export function describeError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body: unknown = err.error;
    if (body && typeof body === 'object' && typeof (body as { error?: unknown }).error === 'string') {
      return (body as { error: string }).error;
    }
    if (typeof body === 'string' && body.trim()) return body;
    if (err.status === 0) return 'Could not reach the server.';
    return `Request failed: ${err.status} ${err.statusText}`;
  }
  if (err instanceof Error) return err.message;
  return 'Something went wrong.';
}
