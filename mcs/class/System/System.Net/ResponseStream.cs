//
// System.Net.ResponseStream
//
// Author:
//	Gonzalo Paniagua Javier (gonzalo@novell.com)
//
// Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

#if SECURITY_DEP

using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Runtime.InteropServices;
namespace System.Net {
	// FIXME: Does this buffer the response until Close?
	// Update: we send a single packet for the first non-chunked Write
	// What happens when we set content-length to X and write X-1 bytes then close?
	// what if we don't set content-length at all?
	class ResponseStream : Stream
	{
		HttpListenerResponse response;
		bool ignore_errors;
		bool disposed;
		bool trailer_sent;
		Stream stream;

		internal ResponseStream (Stream stream, HttpListenerResponse response, bool ignore_errors)
		{
			this.response = response;
			this.ignore_errors = ignore_errors;
			this.stream = stream;
		}

		public override bool CanRead {
			get { return false; }
		}

		public override bool CanSeek {
			get { return false; }
		}

		public override bool CanWrite {
			get { return true; }
		}

		public override long Length {
			get { throw new NotSupportedException (); }
		}

		public override long Position {
			get { throw new NotSupportedException (); }
			set { throw new NotSupportedException (); }
		}


		public override void Close ()
		{
			if (disposed == false) {
				disposed = true;
				byte [] bytes = null;
				MemoryStream ms = GetHeaders (true);
				bool chunked = response.SendChunked;
				if (ms != null) {
					long start = ms.Position;
					if (chunked && !trailer_sent) {
						bytes = GetChunkSizeBytes (0, true);
						ms.Position = ms.Length;
						ms.Write (bytes, 0, bytes.Length);
					}
					InternalWrite (ms.GetBuffer (), (int) start, (int) (ms.Length - start));
					trailer_sent = true;
				} else if (chunked && !trailer_sent) {
					bytes = GetChunkSizeBytes (0, true);
					InternalWrite (bytes, 0, bytes.Length);
					trailer_sent = true;
				}
				response.Close ();
			}
		}

		MemoryStream GetHeaders (bool closing)
		{
			// SendHeaders works on shared headers
			lock (response.headers_lock) {
				if (response.HeadersSent)
					return null;
				MemoryStream ms = new MemoryStream ();
				response.SendHeaders (closing, ms);
				return ms;
			}
		}

		public override void Flush ()
		{
		}

		static byte [] crlf = new byte [] { 13, 10 };
		static byte [] GetChunkSizeBytes (int size, bool final)
		{
			string str = String.Format ("{0:x}\r\n{1}", size, final ? "\r\n" : "");
			return Encoding.ASCII.GetBytes (str);
		}

		internal void InternalWrite (byte [] buffer, int offset, int count)
		{
			if (ignore_errors) {
				try {
					stream.Write (buffer, offset, count);
				} catch { }
			} else {
				stream.Write (buffer, offset, count);
			}
		}

		public override void Write (byte [] buffer, int offset, int count)
		{
			if (disposed)
				throw new ObjectDisposedException (GetType ().ToString ());

			byte [] bytes = null;
			MemoryStream ms = GetHeaders (false);
			bool chunked = response.SendChunked;
			if (ms != null) {
				long start = ms.Position; // After the possible preamble for the encoding
				ms.Position = ms.Length;
				if (chunked) {
					bytes = GetChunkSizeBytes (count, false);
					ms.Write (bytes, 0, bytes.Length);
				}

				int new_count = Math.Min (count, 16384 - (int) ms.Position + (int) start);
				ms.Write (buffer, offset, new_count);
				count -= new_count;
				offset += new_count;
				InternalWrite (ms.GetBuffer (), (int) start, (int) (ms.Length - start));
				ms.SetLength (0);
				ms.Capacity = 0; // 'dispose' the buffer in ms.
			} else if (chunked) {
				bytes = GetChunkSizeBytes (count, false);
				InternalWrite (bytes, 0, bytes.Length);
			}

			if (count > 0)
				InternalWrite (buffer, offset, count);
			if (chunked)
				InternalWrite (crlf, 0, 2);
		}

		public override IAsyncResult BeginWrite (byte [] buffer, int offset, int count,
							AsyncCallback cback, object state)
		{
			if (disposed)
				throw new ObjectDisposedException (GetType ().ToString ());

			byte [] bytes = null;
			MemoryStream ms = GetHeaders (false);
			bool chunked = response.SendChunked;
			if (ms != null) {
				long start = ms.Position;
				ms.Position = ms.Length;
				if (chunked) {
					bytes = GetChunkSizeBytes (count, false);
					ms.Write (bytes, 0, bytes.Length);
				}
				ms.Write (buffer, offset, count);
				buffer = ms.GetBuffer ();
				offset = (int) start;
				count = (int) (ms.Position - start);
			} else if (chunked) {
				bytes = GetChunkSizeBytes (count, false);
				InternalWrite (bytes, 0, bytes.Length);
			}

			return stream.BeginWrite (buffer, offset, count, cback, state);
		}

		public override void EndWrite (IAsyncResult ares)
		{
			if (disposed)
				throw new ObjectDisposedException (GetType ().ToString ());

			if (ignore_errors) {
				try {
					stream.EndWrite (ares);
					if (response.SendChunked)
						stream.Write (crlf, 0, 2);
				} catch { }
			} else {
				stream.EndWrite (ares);
				if (response.SendChunked)
					stream.Write (crlf, 0, 2);
			}
		}

		public override int Read ([In,Out] byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException ();
		}

		public override IAsyncResult BeginRead (byte [] buffer, int offset, int count,
							AsyncCallback cback, object state)
		{
			throw new NotSupportedException ();
		}

		public override int EndRead (IAsyncResult ares)
		{
			throw new NotSupportedException ();
		}

		public override long Seek (long offset, SeekOrigin origin)
		{
			throw new NotSupportedException ();
		}

		public override void SetLength (long value)
		{
			throw new NotSupportedException ();
		}
	}
}
#endif

