﻿'    WakeOnLAN - Wake On LAN
'    Copyright (C) 2004-2019 Aquila Technology, LLC. <webmaster@aquilatech.com>
'
'    This file is part of WakeOnLAN.
'
'    WakeOnLAN is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    WakeOnLAN is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with WakeOnLAN.  If not, see <http://www.gnu.org/licenses/>.

Imports System.ComponentModel
Imports System.Net.NetworkInformation
Imports System.Xml.Serialization
Imports System.Net
Imports System.Threading

<DebuggerDisplay("{Name,nq}")>
<Serializable()>
<CLSCompliant(True)>
Public Class Machine

    <NonSerialized> Private WithEvents _backgroundWorker As New BackgroundWorker
    <NonSerialized> Private WithEvents ping As New Ping

    Private Declare Function SendARP Lib "iphlpapi.dll" (ByVal DestIP As Int32, ByVal SrcIP As Int32, ByVal pMacAddr As Byte(), ByRef PhyAddrLen As Integer) As Integer

    Public Enum ShutdownMethods As Integer
        WMI = 0
        Custom = 1
        Legacy = 2
    End Enum

	Public Enum StatusCodes As Integer
		Online
		Offline
		Unknown
		Fail
		Uninitialized
	End Enum

	Public Enum TraceMethods As Integer
		WriteLine
		Indent
		UnIndent
	End Enum

	Public Property Name As String = String.Empty
    Public Property MAC As String = String.Empty
    Public Property IP As String = String.Empty
    Public Property Broadcast As String = String.Empty
    Public Property Netbios As String = String.Empty
    Public Property Method As Integer = 0
    Public Property Emergency As Boolean = False
    Public Property ShutdownCommand As String = String.Empty
    Public Property Group As String = String.Empty
    Public Property UDPPort As Integer = 9
    Public Property TTL As Integer = 128
    Public Property RemoteCommand As String = String.Empty
    Public Property Note As String = String.Empty
    Public Property UserID As String = String.Empty
    Public Property Password As String = String.Empty
    Public Property Domain As String = String.Empty
    Public Property ShutdownMethod As ShutdownMethods = ShutdownMethods.WMI
    Public Property KeepAlive As Boolean = False
    Public Property RepeatCount As Integer = 1

	<XmlIgnore()> Public Status As StatusCodes = StatusCodes.Uninitialized
	<NonSerialized> <XmlIgnore()> Public Reply As PingReply
    <NonSerialized> <XmlIgnore()> Public Pool As Semaphore
    <NonSerialized> <XmlIgnore()> Const data As String = "abcdefghijklmnopqrstuvwabcdefghi"
	<NonSerialized> <XmlIgnore()> Dim buffer As Byte() = Text.Encoding.ASCII.GetBytes(data)
	<NonSerialized> <XmlIgnore()> Public message As String

	Public Event StatusChange(ByVal Name As String, ByVal status As StatusCodes, ByVal IpAddress As String)
	Public Event TraceEvent(ByVal s As String, ByVal method As TraceMethods)

	Public Sub New()
        _backgroundWorker.WorkerSupportsCancellation = True
    End Sub

    Public Overrides Function ToString() As String
        Return "Machine: " & Name
    End Function

    Public Sub Run()
		If _backgroundWorker.IsBusy Then Exit Sub

		_backgroundWorker.WorkerReportsProgress = True
        If String.IsNullOrEmpty(Netbios) Then
            If String.IsNullOrEmpty(IP) Then Exit Sub
            _backgroundWorker.RunWorkerAsync(IP)
        Else
            _backgroundWorker.RunWorkerAsync(Netbios)
        End If
    End Sub

    Public Function IsBusy() As Boolean
        Return _backgroundWorker.IsBusy
    End Function

    Public Sub Cancel()
        _backgroundWorker.CancelAsync()
    End Sub

    Private Sub DoWork(ByVal sender As Object, ByVal e As DoWorkEventArgs) Handles _backgroundWorker.DoWork
        Do
            Try
                Dim options As PingOptions = New PingOptions() With {
                    .Ttl = TTL,
                    .DontFragment = True
                }

                Debug.Assert(Not IsNothing(Pool))
                Pool.WaitOne()
                Reply = ping.Send(e.Argument, 10000, buffer, options)

                Select Case Reply.Status
                    Case IPStatus.Success
                        _backgroundWorker.ReportProgress(StatusCodes.Online)

                    Case IPStatus.TimedOut, IPStatus.DestinationNetworkUnreachable, IPStatus.DestinationHostUnreachable
                        _backgroundWorker.ReportProgress(StatusCodes.Offline)

                    Case Else
                        _backgroundWorker.ReportProgress(StatusCodes.Unknown)

                End Select

            Catch ex As Exception
				message = ex.Message
				_backgroundWorker.ReportProgress(StatusCodes.Fail)

			Finally
                Pool.Release()
                Thread.Sleep(2000)

            End Try

        Loop Until _backgroundWorker.CancellationPending = True
    End Sub

	Private Sub backgroundWorker_ProgressChanged(ByVal sender As Object, ByVal e As ProgressChangedEventArgs) Handles _backgroundWorker.ProgressChanged
		Dim newStatus As StatusCodes
        Dim newIpAddress As String = String.Empty

        Try
			If _backgroundWorker.CancellationPending Then Exit Sub

			Select Case e.ProgressPercentage
				Case StatusCodes.Online
					newStatus = StatusCodes.Online
					For Each ipAddress As IPAddress In From IPA1 In Dns.GetHostAddresses(Netbios) Where IPA1.AddressFamily.ToString() = "InterNetwork"

						If IP = String.Empty Then
							newIpAddress = ipAddress.ToString()
						Else
							newIpAddress = IP
						End If

						If Status = StatusCodes.Uninitialized Then
							' if the host is DHCP, try to resolve the correct IP and verify the MAC.
							' if we cannot find a matching interface with our MAC, assume the host is OFFLINE.
							If IP = String.Empty Then
								newStatus = StatusCodes.Offline

								Dim remoteIp As Integer
								Dim remoteMac() As Byte = New Byte(5) {}
								Dim dWord As Integer

								Try
									remoteIp = ipAddress.GetHashCode()
									RaiseEvent TraceEvent(Name & ": dns returned: " & ipAddress.ToString(), TraceMethods.WriteLine)

									If remoteIp <> 0 Then
										dWord = SendARP(remoteIp, 0, remoteMac, remoteMac.Length)
										If dWord = 0 Or dWord = 67 Then
											'
											' we found a matching MAC, the host is officially ONLINE
											' 67 = ERROR_BAD_NET_NAME: if host on another subnet, just ignore the error
											'
											If CompareMac(BitConverter.ToString(remoteMac, 0, remoteMac.Length), MAC) = 0 Or dWord = 67 Then
												newStatus = StatusCodes.Online
												newIpAddress = ipAddress.ToString()
												RaiseEvent TraceEvent(Name & ": match: " & newIpAddress, TraceMethods.WriteLine)
												Exit For
											End If
										End If
									End If

								Catch

								End Try
							End If
						End If
					Next

					If (Status <> newStatus) Then
						Status = newStatus
						RaiseEvent StatusChange(Name, Status, newIpAddress)
					End If

				Case StatusCodes.Offline
					If Status <> StatusCodes.Offline Then
						Status = StatusCodes.Offline
						RaiseEvent StatusChange(Name, Status, String.Empty)
					End If

				Case StatusCodes.Unknown, StatusCodes.Fail, StatusCodes.Uninitialized
					If Status <> StatusCodes.Unknown Then
						Status = StatusCodes.Unknown
						RaiseEvent StatusChange(Name, Status, message)
					End If

			End Select

		Catch ex As Exception
			RaiseEvent TraceEvent("backgroundWorker_ProgressChanged::Exception: " & ex.Message, TraceMethods.WriteLine)
			Debug.Fail(ex.Message)

		End Try

	End Sub

	Private Sub RunWorkerCompleted(ByVal sender As Object, ByVal e As RunWorkerCompletedEventArgs) Handles _backgroundWorker.RunWorkerCompleted
        Try
			Status = StatusCodes.Unknown
			RaiseEvent StatusChange(Name, Status, String.Empty)

        Catch ex As Exception
            Debug.Fail(ex.Message)

        End Try

    End Sub

    Private Function CompareMac(mac1 As String, mac2 As String) As Int32

        Dim _mac1 As String = Replace(mac1, ":", String.Empty)
        _mac1 = Replace(_mac1, "-", String.Empty)

        Dim _mac2 As String = Replace(mac2, ":", String.Empty)
        _mac2 = Replace(_mac2, "-", String.Empty)

        Return StrComp(_mac1, _mac2, CompareMethod.Text)

    End Function

End Class
