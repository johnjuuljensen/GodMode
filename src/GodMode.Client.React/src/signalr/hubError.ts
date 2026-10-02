/** What a failed hub call says: the server's own message, without SignalR's "An unexpected error occurred invoking … HubException: ". */
export const hubErrorMessage = (err: unknown) => err instanceof Error ? err.message.replace(/^.*HubException: /, '') : String(err);
