using APBD_07.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace APBD_07.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName)
    {
        const string sql = """
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
            """;

        var result = new List<AppointmentListDto>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Status", System.Data.SqlDbType.NVarChar, 30).Value =
            (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", System.Data.SqlDbType.NVarChar, 80).Value =
            (object?)patientLastName ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Reason = reader.GetString(reader.GetOrdinal("Reason")),
                PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail"))
            });
        }

        return Ok(result);
    }
[HttpGet("{idAppointment:int}")]
public async Task<IActionResult> GetAppointmentDetails(int idAppointment)
{
    const string sql = """
        SELECT
            a.IdAppointment,
            a.AppointmentDate,
            a.Status,
            a.Reason,
            a.InternalNotes,
            a.CreatedAt,
            p.FirstName + N' ' + p.LastName AS PatientFullName,
            p.Email AS PatientEmail,
            p.PhoneNumber AS PatientPhone,
            d.FirstName + N' ' + d.LastName AS DoctorFullName,
            d.LicenseNumber AS DoctorLicenseNumber,
            s.Name AS DoctorSpecialization
        FROM dbo.Appointments a
        JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
        JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
        JOIN dbo.Specializations s ON s.IdSpecialization = d.IdSpecialization
        WHERE a.IdAppointment = @IdAppointment;
        """;

    await using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync();

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@IdAppointment", System.Data.SqlDbType.Int).Value = idAppointment;

    await using var reader = await command.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
    {
        return NotFound(new ErrorResponseDto
        {
            Message = $"Appointment with id {idAppointment} not found."
        });
    }

    var dto = new AppointmentDetailsDto
    {
        IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
        AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
        Status = reader.GetString(reader.GetOrdinal("Status")),
        Reason = reader.GetString(reader.GetOrdinal("Reason")),
        InternalNotes = reader.IsDBNull(reader.GetOrdinal("InternalNotes"))
            ? null
            : reader.GetString(reader.GetOrdinal("InternalNotes")),
        CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
        PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
        PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail")),
        PatientPhone = reader.GetString(reader.GetOrdinal("PatientPhone")),
        DoctorFullName = reader.GetString(reader.GetOrdinal("DoctorFullName")),
        DoctorLicenseNumber = reader.GetString(reader.GetOrdinal("DoctorLicenseNumber")),
        DoctorSpecialization = reader.GetString(reader.GetOrdinal("DoctorSpecialization"))
    };

    return Ok(dto);
}
[HttpPost]
public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto dto)
{
    if (string.IsNullOrWhiteSpace(dto.Reason))
    {
        return BadRequest(new ErrorResponseDto { Message = "Reason is required." });
    }

    if (dto.Reason.Length > 250)
    {
        return BadRequest(new ErrorResponseDto { Message = "Reason must be at most 250 characters." });
    }

    if (dto.AppointmentDate <= DateTime.UtcNow)
    {
        return BadRequest(new ErrorResponseDto { Message = "Appointment date must be in the future." });
    }

    await using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync();

    const string patientCheckSql = "SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient;";
    await using (var patientCmd = new SqlCommand(patientCheckSql, connection))
    {
        patientCmd.Parameters.Add("@IdPatient", System.Data.SqlDbType.Int).Value = dto.IdPatient;
        var patientIsActive = await patientCmd.ExecuteScalarAsync();

        if (patientIsActive is null)
        {
            return BadRequest(new ErrorResponseDto { Message = $"Patient with id {dto.IdPatient} does not exist." });
        }
        if (!(bool)patientIsActive)
        {
            return BadRequest(new ErrorResponseDto { Message = $"Patient with id {dto.IdPatient} is not active." });
        }
    }

    const string doctorCheckSql = "SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor;";
    await using (var doctorCmd = new SqlCommand(doctorCheckSql, connection))
    {
        doctorCmd.Parameters.Add("@IdDoctor", System.Data.SqlDbType.Int).Value = dto.IdDoctor;
        var doctorIsActive = await doctorCmd.ExecuteScalarAsync();

        if (doctorIsActive is null)
        {
            return BadRequest(new ErrorResponseDto { Message = $"Doctor with id {dto.IdDoctor} does not exist." });
        }
        if (!(bool)doctorIsActive)
        {
            return BadRequest(new ErrorResponseDto { Message = $"Doctor with id {dto.IdDoctor} is not active." });
        }
    }

    const string conflictSql = """
        SELECT COUNT(*) FROM dbo.Appointments
        WHERE IdDoctor = @IdDoctor
          AND AppointmentDate = @AppointmentDate
          AND Status = N'Scheduled';
        """;
    await using (var conflictCmd = new SqlCommand(conflictSql, connection))
    {
        conflictCmd.Parameters.Add("@IdDoctor", System.Data.SqlDbType.Int).Value = dto.IdDoctor;
        conflictCmd.Parameters.Add("@AppointmentDate", System.Data.SqlDbType.DateTime2).Value = dto.AppointmentDate;
        var conflicts = (int)(await conflictCmd.ExecuteScalarAsync() ?? 0);

        if (conflicts > 0)
        {
            return Conflict(new ErrorResponseDto
            {
                Message = $"Doctor {dto.IdDoctor} already has a scheduled appointment at {dto.AppointmentDate:O}."
            });
        }
    }

    const string insertSql = """
        INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
        VALUES (@IdPatient, @IdDoctor, @AppointmentDate, N'Scheduled', @Reason);
        SELECT CAST(SCOPE_IDENTITY() AS INT);
        """;
    int newId;
    await using (var insertCmd = new SqlCommand(insertSql, connection))
    {
        insertCmd.Parameters.Add("@IdPatient", System.Data.SqlDbType.Int).Value = dto.IdPatient;
        insertCmd.Parameters.Add("@IdDoctor", System.Data.SqlDbType.Int).Value = dto.IdDoctor;
        insertCmd.Parameters.Add("@AppointmentDate", System.Data.SqlDbType.DateTime2).Value = dto.AppointmentDate;
        insertCmd.Parameters.Add("@Reason", System.Data.SqlDbType.NVarChar, 250).Value = dto.Reason;

        newId = (int)(await insertCmd.ExecuteScalarAsync() ?? 0);
    }

    return CreatedAtAction(
        nameof(GetAppointmentDetails),
        new { idAppointment = newId },
        new { IdAppointment = newId });
}
[HttpPut("{idAppointment:int}")]
public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto dto)
{
    if (string.IsNullOrWhiteSpace(dto.Reason))
    {
        return BadRequest(new ErrorResponseDto { Message = "Reason is required." });
    }

    if (dto.Reason.Length > 250)
    {
        return BadRequest(new ErrorResponseDto { Message = "Reason must be at most 250 characters." });
    }

    var allowedStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
    if (!allowedStatuses.Contains(dto.Status))
    {
        return BadRequest(new ErrorResponseDto
        {
            Message = "Status must be one of: Scheduled, Completed, Cancelled."
        });
    }

    await using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync();

    const string currentSql = "SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
    string currentStatus;
    DateTime currentDate;
    await using (var currentCmd = new SqlCommand(currentSql, connection))
    {
        currentCmd.Parameters.Add("@IdAppointment", System.Data.SqlDbType.Int).Value = idAppointment;
        await using var reader = await currentCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return NotFound(new ErrorResponseDto { Message = $"Appointment with id {idAppointment} not found." });
        }
        currentStatus = reader.GetString(0);
        currentDate = reader.GetDateTime(1);
    }

    if (currentStatus == "Completed" && dto.AppointmentDate != currentDate)
    {
        return Conflict(new ErrorResponseDto
        {
            Message = "Cannot change appointment date for a completed appointment."
        });
    }

    const string patientCheckSql = "SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient;";
    await using (var patientCmd = new SqlCommand(patientCheckSql, connection))
    {
        patientCmd.Parameters.Add("@IdPatient", System.Data.SqlDbType.Int).Value = dto.IdPatient;
        var patientIsActive = await patientCmd.ExecuteScalarAsync();
        if (patientIsActive is null)
        {
            return BadRequest(new ErrorResponseDto { Message = $"Patient with id {dto.IdPatient} does not exist." });
        }
        if (!(bool)patientIsActive)
        {
            return BadRequest(new ErrorResponseDto { Message = $"Patient with id {dto.IdPatient} is not active." });
        }
    }

    const string doctorCheckSql = "SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor;";
    await using (var doctorCmd = new SqlCommand(doctorCheckSql, connection))
    {
        doctorCmd.Parameters.Add("@IdDoctor", System.Data.SqlDbType.Int).Value = dto.IdDoctor;
        var doctorIsActive = await doctorCmd.ExecuteScalarAsync();
        if (doctorIsActive is null)
        {
            return BadRequest(new ErrorResponseDto { Message = $"Doctor with id {dto.IdDoctor} does not exist." });
        }
        if (!(bool)doctorIsActive)
        {
            return BadRequest(new ErrorResponseDto { Message = $"Doctor with id {dto.IdDoctor} is not active." });
        }
    }

    if (dto.AppointmentDate != currentDate)
    {
        const string conflictSql = """
            SELECT COUNT(*) FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND Status = N'Scheduled'
              AND IdAppointment <> @IdAppointment;
            """;
        await using var conflictCmd = new SqlCommand(conflictSql, connection);
        conflictCmd.Parameters.Add("@IdDoctor", System.Data.SqlDbType.Int).Value = dto.IdDoctor;
        conflictCmd.Parameters.Add("@AppointmentDate", System.Data.SqlDbType.DateTime2).Value = dto.AppointmentDate;
        conflictCmd.Parameters.Add("@IdAppointment", System.Data.SqlDbType.Int).Value = idAppointment;
        var conflicts = (int)(await conflictCmd.ExecuteScalarAsync() ?? 0);

        if (conflicts > 0)
        {
            return Conflict(new ErrorResponseDto
            {
                Message = $"Doctor {dto.IdDoctor} already has a scheduled appointment at {dto.AppointmentDate:O}."
            });
        }
    }

    const string updateSql = """
        UPDATE dbo.Appointments
        SET IdPatient = @IdPatient,
            IdDoctor = @IdDoctor,
            AppointmentDate = @AppointmentDate,
            Status = @Status,
            Reason = @Reason,
            InternalNotes = @InternalNotes
        WHERE IdAppointment = @IdAppointment;
        """;
    await using (var updateCmd = new SqlCommand(updateSql, connection))
    {
        updateCmd.Parameters.Add("@IdPatient", System.Data.SqlDbType.Int).Value = dto.IdPatient;
        updateCmd.Parameters.Add("@IdDoctor", System.Data.SqlDbType.Int).Value = dto.IdDoctor;
        updateCmd.Parameters.Add("@AppointmentDate", System.Data.SqlDbType.DateTime2).Value = dto.AppointmentDate;
        updateCmd.Parameters.Add("@Status", System.Data.SqlDbType.NVarChar, 30).Value = dto.Status;
        updateCmd.Parameters.Add("@Reason", System.Data.SqlDbType.NVarChar, 250).Value = dto.Reason;
        updateCmd.Parameters.Add("@InternalNotes", System.Data.SqlDbType.NVarChar, 500).Value =
            (object?)dto.InternalNotes ?? DBNull.Value;
        updateCmd.Parameters.Add("@IdAppointment", System.Data.SqlDbType.Int).Value = idAppointment;

        await updateCmd.ExecuteNonQueryAsync();
    }

    return Ok(new { IdAppointment = idAppointment });
}
[HttpDelete("{idAppointment:int}")]
public async Task<IActionResult> DeleteAppointment(int idAppointment)
{
    await using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync();

    const string statusSql = "SELECT Status FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
    string? currentStatus;
    await using (var statusCmd = new SqlCommand(statusSql, connection))
    {
        statusCmd.Parameters.Add("@IdAppointment", System.Data.SqlDbType.Int).Value = idAppointment;
        currentStatus = (string?)await statusCmd.ExecuteScalarAsync();
    }

    if (currentStatus is null)
    {
        return NotFound(new ErrorResponseDto { Message = $"Appointment with id {idAppointment} not found." });
    }

    if (currentStatus == "Completed")
    {
        return Conflict(new ErrorResponseDto
        {
            Message = "Cannot delete a completed appointment."
        });
    }

    const string deleteSql = "DELETE FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
    await using (var deleteCmd = new SqlCommand(deleteSql, connection))
    {
        deleteCmd.Parameters.Add("@IdAppointment", System.Data.SqlDbType.Int).Value = idAppointment;
        await deleteCmd.ExecuteNonQueryAsync();
    }

    return NoContent(); 
}
}