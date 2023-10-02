using System.Net;
using System.Security.Claims;
using Identity;
using IdentityModel;
using JetBrains.Annotations;
using Kochnev.Auth.Private.Client.Api;
using Kochnev.Auth.Private.Client.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[PublicAPI]
public sealed record CallbackResponse(
    string LoginResponseId
);

[PublicAPI]
public sealed record LoginRequest(
    string Username,
    string Password,
    bool IsPersistent,
    string LoginRequestId
);

[PublicAPI]
public sealed class LoginResponse
{
    [PublicAPI]
    public sealed record ErrorDto(string ErrorCode);

    public enum ErrorCodeDto
    {
        Failed = 1,
        LockedOut = 2,
        NotAllowed = 3,
        RequiresTwoFactor = 4,
    }

    public bool IsSuccess { get; }
    public string? LoginResponseId { get; }
    public ErrorDto[] Errors { get; }

    private LoginResponse(
        bool isSuccess,
        string? loginResponseId,
        ErrorDto[] errors)
    {
        IsSuccess = isSuccess;
        LoginResponseId = loginResponseId;
        Errors = errors;
    }

    public static LoginResponse Success(string? loginResponseId = null)
    {
        return new LoginResponse(
            isSuccess: true,
            loginResponseId: loginResponseId,
            errors: Array.Empty<ErrorDto>()
        );
    }

    public static LoginResponse Failure(ErrorCodeDto[] errors, string? loginResponseId = null)
    {
        return new LoginResponse(
            isSuccess: true,
            loginResponseId: loginResponseId,
            errors: errors
                .Distinct()
                .Select(x => new ErrorDto(ErrorCode: x.ToString("G").ToLowerInvariant()))
                .ToArray()
        );
    }
}

[PublicAPI]
public sealed record RegisterRequest(
    string Username,
    string Password,
    string PasswordConfirmation,
    bool IsPersistent,
    string LoginRequestId
);

[PublicAPI]
public sealed class RegisterResponse
{
    [PublicAPI]
    public sealed record ErrorDto(string ErrorCode);

    public enum ErrorCodeDto
    {
        // @formatter:off
        UnknownError                       =  1,
        ConfirmationDoesNotMatchToPassword =  2,
        InvalidUserName                    =  3,
        InvalidEmail                       =  4,
        DuplicateUserName                  =  5,
        DuplicateEmail                     =  6,
        UserAlreadyHasPassword             =  7,
        PasswordTooShort                   =  8,
        PasswordRequiresUniqueChars        =  9,
        PasswordRequiresNonAlphanumeric    = 10,
        PasswordRequiresDigit              = 11,
        PasswordRequiresLower              = 12,
        PasswordRequiresUpper              = 13,
        // @formatter:on
    }

    public bool IsSuccess { get; }
    public string? LoginResponseId { get; }
    public ErrorDto[] Errors { get; }

    private RegisterResponse(
        bool isSuccess,
        string? loginResponseId,
        ErrorDto[] errors)
    {
        IsSuccess = isSuccess;
        LoginResponseId = loginResponseId;
        Errors = errors;
    }

    public static RegisterResponse Success(string? loginResponseId = null)
    {
        return new RegisterResponse(
            isSuccess: true,
            loginResponseId: loginResponseId,
            errors: Array.Empty<ErrorDto>()
        );
    }

    public static RegisterResponse Failure(ErrorCodeDto[] errors, string? loginResponseId = null)
    {
        return new RegisterResponse(
            isSuccess: true,
            loginResponseId: loginResponseId,
            errors: errors
                .Distinct()
                .Select(x => new ErrorDto(ErrorCode: x.ToString("G").ToLowerInvariant()))
                .ToArray()
        );
    }
}

[PublicAPI]
public sealed record LoginGoogleRequest(
    string LoginRequestId,
    bool IsPersistent
);

public static class EnumerableExtensions
{
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source) where T : class =>
        from item in source
        where item is not null
        select item;

    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source) where T : struct =>
        from item in source
        where item.HasValue
        select item.Value;
}

[ApiController]
[Route("account")]
public class AccountController : ControllerBase
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILoginCallbackApi _loginCallbackApi;
    private readonly IConfiguration _configuration;

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        ILoginCallbackApi loginCallbackApi,
        IConfiguration configuration)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _loginCallbackApi = loginCallbackApi;
        _configuration = configuration;
    }

    [ProducesResponseType(typeof(RegisterResponse), 200)]
    [ProducesResponseType(typeof(RegisterResponse), 400)]
    [ProducesResponseType(500)]
    [HttpPost]
    [Route("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        if (request.Password != request.PasswordConfirmation)
        {
            var response = RegisterResponse.Failure(
                errors: new[] { RegisterResponse.ErrorCodeDto.ConfirmationDoesNotMatchToPassword }
            );
            return BadRequest(response);
        }

        var user = new ApplicationUser
        {
            Id = Random.Shared.Next(1, 100000).ToString(), // todo: remove
            UserName = request.Username,
        };
        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            var response = RegisterResponse.Failure(
                errors: createResult.Errors.Select(ToDto).ToArray()
            );
            return BadRequest(response);
        }

        var result = await _signInManager.PasswordSignInAsync(
            user: user,
            password: request.Password,
            isPersistent: request.IsPersistent,
            lockoutOnFailure: false
        );
        if (!result.Succeeded)
        {
            var response = RegisterResponse.Failure(
                errors: new[] { RegisterResponse.ErrorCodeDto.UnknownError }
            );
            return BadRequest(response);
        }

        {
            var loginResponseId = await NotifySignInSuccess(request.LoginRequestId, user.Id, ct);
            return Ok(new { LoginResponseId = loginResponseId, });
        }
    }

    [ProducesResponseType(typeof(LoginResponse), 200)]
    [ProducesResponseType(typeof(LoginResponse), 400)]
    [ProducesResponseType(500)]
    [HttpPost]
    [Route("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await _userManager.FindByNameAsync(request.Username);
        if (user == null)
        {
            var response = LoginResponse.Failure(
                errors: new[] { LoginResponse.ErrorCodeDto.Failed }
            );
            return BadRequest(response);
        }

        var result = await _signInManager.PasswordSignInAsync(
            userName: request.Username,
            password: request.Password,
            isPersistent: request.IsPersistent,
            lockoutOnFailure: false
        );

        if (!result.Succeeded)
        {
            var errorCode = result switch
            {
                // @formatter:off
                { IsNotAllowed: true }      => LoginResponse.ErrorCodeDto.NotAllowed,
                { IsLockedOut: true }       => LoginResponse.ErrorCodeDto.LockedOut,
                { RequiresTwoFactor: true } => LoginResponse.ErrorCodeDto.RequiresTwoFactor,
                { Succeeded: false }        => LoginResponse.ErrorCodeDto.Failed,
                _                           => LoginResponse.ErrorCodeDto.Failed,
                // @formatter:on
            };
            var response = LoginResponse.Failure(
                errors: new[] { errorCode }
            );
            return BadRequest(response);
        }

        {
            var loginResponseId = await NotifySignInSuccess(request.LoginRequestId, user.Id, ct);
            var response = LoginResponse.Success(loginResponseId: loginResponseId);
            return Ok(response);
        }
    }

    [HttpGet]
    [Route("login/google")]
    public IActionResult LoginGoogle([FromQuery] LoginGoogleRequest request, CancellationToken ct)
    {
        var hostUrl = $"{HttpContext.Request.Scheme}://{HttpContext.Request.Host}";
        var props = new AuthenticationProperties
        {
            RedirectUri = $"{hostUrl}/account/login/google/callback",
            Items =
            {
                { "LoginRequestId", request.LoginRequestId },
                { "LoginProvider", "Google" }, // used to enable await _signInManager.GetExternalLoginInfoAsync()
                { "IsPersistent", request.IsPersistent ? "1" : "0" },
            },
        };

        return Challenge(props, "Google");
    }

    [Authorize(AuthenticationSchemes = "Google")]
    [HttpGet]
    [Route("login/google/callback")]
    public async Task<IActionResult> LoginGoogleCallback(CancellationToken ct)
    {
        var externalAuthResult = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        if (externalAuthResult.Succeeded != true)
        {
            throw new Exception("External authentication error");
        }

        var loginRequestId = externalAuthResult.Properties.Items["LoginRequestId"];
        if (loginRequestId == null)
        {
            throw new Exception("LoginRequestId is null");
        }

        var isPersistent = externalAuthResult.Properties.Items["IsPersistent"] == "1";

        var denjiBaseUrl = _configuration.GetValue<string>("Denji:BaseUrl");
        if (denjiBaseUrl == null)
        {
            throw new ApplicationException();
        }

        var cosmoBaseUrl = _configuration.GetValue<string>("Cosmo:BaseUrl");
        if (cosmoBaseUrl == null)
        {
            throw new ApplicationException();
        }

        var externalUser = externalAuthResult.Principal;

        var userIdClaim =
            externalUser.FindFirst(JwtClaimTypes.Subject) ??
            externalUser.FindFirst(ClaimTypes.NameIdentifier) ??
            throw new Exception("Unknown userid");

        const string provider = "Google";
        var providerUserId = userIdClaim.Value;

        var userByLogin = await _userManager.FindByLoginAsync("Google", userIdClaim.Value);
        if (userByLogin != null)
        {
            // var internalUserPrincipal = await _signInManager.CreateUserPrincipalAsync(userByLogin);
            // var internalUserLocalSignInProps = new AuthenticationProperties();
            // await HttpContext.SignInAsync(
            //     IdentityConstants.ApplicationScheme,
            //     internalUserPrincipal,
            //     internalUserLocalSignInProps
            // );

            await _signInManager.SignInAsync(
                user: userByLogin,
                isPersistent: isPersistent
            );

            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            var loginResponseId = await NotifySignInSuccess(loginRequestId, userByLogin.Id, ct);

            var returnUrl = $"{denjiBaseUrl}/connect/authorize/callback?loginResponseId={loginResponseId}";
            return Redirect(returnUrl);
        }

        var internalUser = new ApplicationUser { UserName = Guid.NewGuid().ToString(), };
        var internalUserResult = await _userManager.CreateAsync(internalUser);
        if (!internalUserResult.Succeeded)
        {
            throw new Exception(internalUserResult.Errors.First().Description);
        }

        var addLoginResult = await _userManager.AddLoginAsync(
            internalUser,
            new UserLoginInfo(provider, providerUserId, provider)
        );
        if (!addLoginResult.Succeeded)
        {
            throw new Exception(addLoginResult.Errors.First().Description);
        }

        {
            var internalUserPrincipal = await _signInManager.CreateUserPrincipalAsync(internalUser);
            var internalUserLocalSignInProps = new AuthenticationProperties();
            await HttpContext.SignInAsync(
                IdentityConstants.ApplicationScheme,
                internalUserPrincipal,
                internalUserLocalSignInProps
            );

            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            var loginResponseId = await NotifySignInSuccess(loginRequestId, internalUser.Id, ct);

            var returnUrl = $"{denjiBaseUrl}/connect/authorize/callback?loginResponseId={loginResponseId}";
            return Redirect(returnUrl);
        }
    }

    [HttpPost]
    [Route("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await _signInManager.SignOutAsync();
        return Ok();
    }

    [HttpGet]
    [Route("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        await Task.CompletedTask;
        return Ok(new
        {
            IsSignedIn = _signInManager.IsSignedIn(User),
        });
    }

    private async Task<string> NotifySignInSuccess(string loginRequestId, string subjectId, CancellationToken ct)
    {
        var request = new ApiApiPrivateLoginCallbackAcceptRequest
        {
            LoginRequestId = loginRequestId,
            SubjectId = subjectId,
        };
        var response = await _loginCallbackApi.ApiPrivateLoginCallbackAcceptPostWithHttpInfoAsync(
            apiApiPrivateLoginCallbackAcceptRequest: request,
            cancellationToken: ct
        );
        return response.StatusCode == HttpStatusCode.OK
            ? response.Data.LoginResponseId
            : throw new Exception();
    }

    private static RegisterResponse.ErrorCodeDto ToDto(IdentityError identityError)
    {
        // @formatter:off
        return identityError switch
        {
            { Code: "InvalidUserName" }                 => RegisterResponse.ErrorCodeDto.InvalidUserName,
            { Code: "InvalidEmail" }                    => RegisterResponse.ErrorCodeDto.InvalidEmail,
            { Code: "DuplicateUserName" }               => RegisterResponse.ErrorCodeDto.DuplicateUserName,
            { Code: "DuplicateEmail" }                  => RegisterResponse.ErrorCodeDto.DuplicateEmail,
            { Code: "UserAlreadyHasPassword" }          => RegisterResponse.ErrorCodeDto.UserAlreadyHasPassword,
            { Code: "PasswordTooShort" }                => RegisterResponse.ErrorCodeDto.PasswordTooShort,
            { Code: "PasswordRequiresUniqueChars" }     => RegisterResponse.ErrorCodeDto.PasswordRequiresUniqueChars,
            { Code: "PasswordRequiresNonAlphanumeric" } => RegisterResponse.ErrorCodeDto.PasswordRequiresNonAlphanumeric,
            { Code: "PasswordRequiresDigit" }           => RegisterResponse.ErrorCodeDto.PasswordRequiresDigit,
            { Code: "PasswordRequiresLower" }           => RegisterResponse.ErrorCodeDto.PasswordRequiresLower,
            { Code: "PasswordRequiresUpper" }           => RegisterResponse.ErrorCodeDto.PasswordRequiresUpper,
            //
            { Code: "InvalidRoleName" }                 => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "UserLockoutNotEnabled" }           => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "UserAlreadyInRole" }               => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "UserNotInRole" }                   => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "DuplicateRoleName" }               => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "LoginAlreadyAssociated" }          => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "RecoveryCodeRedemptionFailed" }    => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "InvalidToken" }                    => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "PasswordMismatch" }                => RegisterResponse.ErrorCodeDto.UnknownError,
            { Code: "ConcurrencyFailure" }              => RegisterResponse.ErrorCodeDto.UnknownError,
            _                                           => RegisterResponse.ErrorCodeDto.UnknownError,
        };
        // @formatter:on
    }
}